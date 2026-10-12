#include "audio.cpp"
#include <FLAC/stream_encoder.h>
#include <filesystem>
#include <fstream>
#include <iostream>

static void require(bool value, const char* message) { if(!value) throw std::runtime_error(message); }
static void put(std::ofstream& stream, uint32_t value, int bytes) { for(int i=0;i<bytes;i++) stream.put(static_cast<char>(value>>(8*i))); }
struct DecoderGate {
    std::mutex mutex;
    std::condition_variable changed;
    bool entered=false,released=false;
};
class GatedSource final : public Source {
    std::shared_ptr<DecoderGate> gate;
    int64_t position=0;
public:
    explicit GatedSource(std::shared_ptr<DecoderGate> state):gate(std::move(state)){info={8000,16,1,16,100000};}
    void seek(int64_t frame) override {position=frame;}
    size_t read(uint8_t* out,size_t frames) override {
        {std::unique_lock lock(gate->mutex);gate->entered=true;gate->changed.notify_all();gate->changed.wait(lock,[&]{return gate->released;});}
        size_t count=static_cast<size_t>(std::min<int64_t>(frames,info.frames-position));
        for(size_t i=0;i<count;i++){auto sample=static_cast<uint16_t>(position++);out[2*i]=static_cast<uint8_t>(sample);out[2*i+1]=static_cast<uint8_t>(sample>>8);}
        return count;
    }
};
class BrokenSource final : public Source {
public:
    BrokenSource(){info={8000,16,1,16,1000};}
    void seek(int64_t) override {}
    size_t read(uint8_t*,size_t) override {throw Failure{E_FAIL,L"Injected decoder failure"};}
};
int main(int argc, char** argv) {
    try {
        std::filesystem::path folder=argc>1?argv[1]:"fixtures";
        std::filesystem::create_directories(folder);
        int checks=0;
        {
            for(size_t bytes:{size_t(2),size_t(3),size_t(4)}){
                std::vector<uint8_t> packet(bytes*64,0xa7),original=packet;
                require(silenceOutput(packet.data(),packet.size(),false)==0&&packet==original,"unmuted samples changed");checks++;
                require(silenceOutput(packet.data(),packet.size(),true)==AUDCLNT_BUFFERFLAGS_SILENT&&std::all_of(packet.begin(),packet.end(),[](uint8_t v){return v==0;}),"mute left residual samples");checks++;
            }
            void* idle=muyu_create();require(idle!=nullptr,"mute fixture could not create idle engine");
            require(muyu_mute(idle,1)==S_OK&&muyu_mute(idle,0)==S_OK,"mute toggle failed without opening audio output");checks++;
            require(muyu_mute(idle,2)==E_INVALIDARG&&muyu_mute(nullptr,1)==E_POINTER,"invalid mute command accepted");checks++;
            ProcessingOptions dop;dop.dop=1;
            require(muyu_configure(idle,dop)==S_OK&&muyu_mute(idle,1)==E_INVALIDARG,"DoP accepted PCM software mute");checks++;
            muyu_destroy(idle);
        }
        {
            auto formats=pcmFormats(44100,16,16,2,SPEAKER_FRONT_LEFT|SPEAKER_FRONT_RIGHT);PcmFormat selected;int opens=0;
            HRESULT result=negotiatePcm(formats,selected,[](const PcmFormat& f){return f.value.Format.wFormatTag==WAVE_FORMAT_PCM?S_OK:AUDCLNT_E_UNSUPPORTED_FORMAT;},[&](const PcmFormat&){opens++;return S_OK;});
            require(result==S_OK&&opens==1&&selected.value.Format.wFormatTag==WAVE_FORMAT_PCM,"legacy stereo PCM driver rejected");checks++;
            opens=0;result=negotiatePcm(formats,selected,[](const PcmFormat&){return S_OK;},[&](const PcmFormat& f){opens++;return f.value.Format.wFormatTag==WAVE_FORMAT_PCM?S_OK:E_INVALIDARG;});
            require(result==S_OK&&opens==2,"Initialize format rejection did not try equivalent descriptor");checks++;
            opens=0;result=negotiatePcm(formats,selected,[](const PcmFormat&){return S_OK;},[&](const PcmFormat&){opens++;return AUDCLNT_E_DEVICE_IN_USE;});
            require(result==AUDCLNT_E_DEVICE_IN_USE&&opens==1,"device busy was hidden by format retry");checks++;
            auto processed16=pcmFormats(44100,16,32,2,3),processed24=pcmFormats(44100,24,32,2,3);
            require(processed16.front().value.Format.wBitsPerSample==16&&processed24.front().value.Format.wBitsPerSample==24,"internal processing width leaked into preferred hardware format");checks++;
            opens=0;result=negotiatePcm(formats,selected,[](const PcmFormat&){return S_OK;},[&](const PcmFormat& f){opens++;return f.value.Format.wFormatTag==WAVE_FORMAT_PCM?S_OK:AUDCLNT_E_ENDPOINT_CREATE_FAILED;});
            require(result==S_OK&&opens==2,"driver false-positive support prevented equivalent endpoint descriptor retry");checks++;
            opens=0;result=negotiatePcm(formats,selected,[](const PcmFormat&){return S_OK;},[&](const PcmFormat&){opens++;return AUDCLNT_E_DEVICE_INVALIDATED;});
            require(result==AUDCLNT_E_DEVICE_INVALIDATED&&opens==1,"disconnected endpoint was hidden by format retry");checks++;
            result=negotiatePcm(formats,selected,[](const PcmFormat& f){return f.value.Format.wBitsPerSample==32&&f.validBits()==32?S_OK:AUDCLNT_E_UNSUPPORTED_FORMAT;},[](const PcmFormat&){return S_OK;});
            require(result==S_OK&&selected.validBits()==32,"lossless zero-padded container rejected");checks++;
            auto multichannel=pcmFormats(96000,24,32,6,0x3f);
            require(std::all_of(multichannel.begin(),multichannel.end(),[](const PcmFormat& f){return f.value.Format.wFormatTag==WAVE_FORMAT_EXTENSIBLE&&f.value.Format.nSamplesPerSec==96000&&f.value.Format.nChannels==6&&f.value.Format.wBitsPerSample>=24;}),"negotiation changed rate, channels or dropped precision");checks++;
        }
        for(int bits : {16,24,32}) {
            const int frames=44100, channels=2, bytes=bits/8;
            std::vector<FLAC__int32> samples(frames*channels);
            std::vector<uint8_t> expected(samples.size()*bytes);
            for(size_t i=0;i<samples.size();i++) {
                // Quiet, deterministic input; include signed extrema at the end for codec checks.
                int32_t value=static_cast<int32_t>((i*97)%1024)-512;
                if(i==samples.size()-2) value=bits==32?INT32_MIN:-(1<<(bits-1));
                if(i==samples.size()-1) value=bits==32?INT32_MAX:(1<<(bits-1))-1;
                samples[i]=value;
                for(int b=0;b<bytes;b++) expected[i*bytes+b]=static_cast<uint8_t>(static_cast<uint32_t>(value)>>(8*b));
            }
            auto wav=folder/("reference-"+std::to_string(bits)+".wav");
            { std::ofstream out(wav,std::ios::binary); out.write("RIFF",4); put(out,36+static_cast<uint32_t>(expected.size()),4); out.write("WAVEfmt ",8); put(out,16,4);put(out,1,2);put(out,channels,2);put(out,44100,4);put(out,44100*channels*bytes,4);put(out,channels*bytes,2);put(out,bits,2);out.write("data",4);put(out,static_cast<uint32_t>(expected.size()),4);out.write(reinterpret_cast<char*>(expected.data()),expected.size()); }
            auto flac=folder/("reference-"+std::to_string(bits)+".flac");
            auto encoder=FLAC__stream_encoder_new();
            FLAC__stream_encoder_set_channels(encoder,channels); FLAC__stream_encoder_set_bits_per_sample(encoder,bits); FLAC__stream_encoder_set_sample_rate(encoder,44100); FLAC__stream_encoder_set_total_samples_estimate(encoder,frames);
            require(FLAC__stream_encoder_init_file(encoder,flac.string().c_str(),nullptr,nullptr)==FLAC__STREAM_ENCODER_INIT_STATUS_OK,"FLAC encoder init");
            require(FLAC__stream_encoder_process_interleaved(encoder,samples.data(),frames)!=0,"FLAC encode");
            require(FLAC__stream_encoder_finish(encoder)!=0,"FLAC finish"); FLAC__stream_encoder_delete(encoder);
            for(auto path : {wav,flac}) {
                auto source=openSource(path.wstring().c_str()); require(source->info.bits==bits&&source->info.frames==frames,"source metadata"); checks++;
                std::vector<uint8_t> decoded(expected.size()); require(source->read(decoded.data(),frames)==frames,"full frame count"); require(decoded==expected,"decoded PCM mismatch");checks++;
                for(int frame : {0,1,4095,4096,frames-1}) {
                    source->seek(frame); std::vector<uint8_t> one(channels*bytes);require(source->read(one.data(),1)==1,"seek read");
                    require(std::equal(one.begin(),one.end(),expected.begin()+frame*channels*bytes),"sample seek mismatch");checks++;
                }
                ReadAheadSource buffered(openSource(path.wstring().c_str()),511);
                buffered.seek(0);
                std::vector<uint8_t> streamed(expected.size());
                size_t position=0;
                while(position<static_cast<size_t>(frames)) {
                    size_t request=std::min<size_t>(1+(position*31)%239,frames-position);
                    require(buffered.waitReady(request),"ring producer timeout");
                    require(buffered.read(streamed.data()+position*channels*bytes,request)==request,"ring short read");position+=request;
                }
                require(streamed==expected,"ring wraparound altered PCM");checks++;
                require(buffered.capacityFrames()==511&&buffered.buffered()<=511,"ring capacity changed");checks++;
                for(int frame:{4096,1,4095,frames-1,0}) {
                    buffered.seek(frame);std::vector<uint8_t> one(channels*bytes);require(buffered.read(one.data(),1)==1,"buffered seek frame count");
                    require(std::equal(one.begin(),one.end(),expected.begin()+frame*channels*bytes),"old-generation PCM survived seek");checks++;
                }
                buffered.seek(21755);
                std::vector<uint8_t> boundary(6*channels*bytes);require(buffered.read(boundary.data(),6)==6,"CUE boundary read");
                require(std::equal(boundary.begin(),boundary.end(),expected.begin()+21755*channels*bytes),"CUE boundary inserted or dropped samples");checks++;
            }
        }
        {
            auto gate=std::make_shared<DecoderGate>();
            ReadAheadSource blocked(std::make_unique<GatedSource>(gate),512);
            {std::unique_lock lock(gate->mutex);require(gate->changed.wait_for(lock,std::chrono::seconds(2),[&]{return gate->entered;}),"decoder did not start");}
            auto read=std::async(std::launch::async,[&]{uint8_t output[2];try{blocked.read(output,1);return false;}catch(const Failure& e){return e.code==AUDCLNT_E_BUFFER_ERROR;}});
            bool immediate=read.wait_for(std::chrono::milliseconds(500))==std::future_status::ready;
            // Release before asserting, so failure cannot strand a test thread.
            {std::lock_guard lock(gate->mutex);gate->released=true;}gate->changed.notify_all();
            require(immediate&&read.get(),"consumer waited on blocked decoder I/O");checks++;
            blocked.seek(5000);uint8_t sample[2];require(blocked.read(sample,1)==1&&le16(sample)==5000,"blocked producer seek recovery");checks++;
        }
        {
            ReadAheadSource broken(std::make_unique<BrokenSource>());
            bool rejected=false;try{broken.seek(0);}catch(const Failure& e){rejected=e.message==L"Injected decoder failure";}
            require(rejected,"decoder failure was not propagated");checks++;
        }
        require(nextBoundary({100,200,300},99,300)==100,"trim before boundary");checks++;
        require(nextBoundary({100,200,300},100,300)==200,"trim on boundary");checks++;
        require(nextBoundary({100,200,300},201,300)==300,"trim after boundary");checks++;
        require(nextBoundary({100,200,300},300,300)==300,"trim at end");checks++;
        auto invalid=folder/"truncated.wav";{std::ofstream out(invalid);out<<"RIFF";}
        bool rejected=false;try{openSource(invalid.wstring().c_str());}catch(...){rejected=true;}require(rejected,"truncated WAV accepted");checks++;
        std::cout<<"PASS "<<checks<<" codec checks; fixtures: "<<folder.string()<<"\n";
        return 0;
    } catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;} catch(const Failure& e){std::wcerr<<e.message<<"\n";return 1;}
}
