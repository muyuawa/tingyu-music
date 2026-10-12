#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>
#include <avrt.h>
#include <bcrypt.h>
#include <FLAC/stream_decoder.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <deque>
#include <functional>
#include <future>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)
struct SourceInfo { int rate, bits, channels, containerBits; int64_t frames; int encoding=0; };
struct DeviceInfo { wchar_t id[512], name[256]; int isDefault,state;wchar_t interfaceName[256]; };
struct Status { int state, error, rate, bits, containerBits, channels, shared; int64_t position, duration, bufferedFrames, bufferCapacity, underruns; int outputBufferFrames; };
struct Failure { HRESULT code; std::wstring message; };
static void check(HRESULT hr, const wchar_t* message) { if (FAILED(hr)) throw Failure{hr, message}; }
static uint32_t le32(const uint8_t* p) { return uint32_t(p[0]) | uint32_t(p[1])<<8 | uint32_t(p[2])<<16 | uint32_t(p[3])<<24; }
static uint16_t le16(const uint8_t* p) { return uint16_t(p[0]) | uint16_t(p[1])<<8; }
static void readExact(FILE* f, void* out, size_t n) { if (fread(out,1,n,f)!=n) throw Failure{E_FAIL,L"音频文件不完整或已损坏"}; }

class Source {
public:
    SourceInfo info{};
    virtual ~Source() = default;
    virtual size_t read(uint8_t* out, size_t frames) = 0;
    virtual void seek(int64_t frame) = 0;
};
class WaveSource final : public Source {
    FILE* file = nullptr;
    int64_t dataOffset=0, current=0;
public:
    explicit WaveSource(const wchar_t* path) {
        file=_wfopen(path,L"rb");
        if (!file) throw Failure{HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),L"无法读取音乐文件"};
        try {
            uint8_t head[12]; readExact(file,head,12);
            if (memcmp(head,"RIFF",4)||memcmp(head+8,"WAVE",4)) throw Failure{E_INVALIDARG,L"首版支持标准 RIFF/WAVE PCM，暂不支持 RF64"};
            _fseeki64(file,0,SEEK_END); const int64_t length=_ftelli64(file); _fseeki64(file,12,SEEK_SET);
            bool formatFound=false, dataFound=false; uint32_t dataBytes=0;
            while (_ftelli64(file)+8<=length) {
                uint8_t chunk[8]; readExact(file,chunk,8); uint32_t size=le32(chunk+4); int64_t next=_ftelli64(file)+size+(size&1);
                if (next>length+1) throw Failure{E_FAIL,L"WAV 区块长度超出文件"};
                if (!memcmp(chunk,"fmt ",4)) {
                    if(size<16||size>4096) throw Failure{E_FAIL,L"WAV 格式区块无效"};
                    std::vector<uint8_t> fmt(size); readExact(file,fmt.data(),size);
                    int tag=le16(fmt.data()); info.channels=le16(fmt.data()+2); info.rate=static_cast<int>(le32(fmt.data()+4)); info.containerBits=le16(fmt.data()+14); info.bits=info.containerBits;
                    if(tag==WAVE_FORMAT_EXTENSIBLE&&size>=40) {
                        if(le16(fmt.data()+16)<22 || memcmp(fmt.data()+24,&KSDATAFORMAT_SUBTYPE_PCM,sizeof(GUID))) throw Failure{E_NOTIMPL,L"WAV 扩展格式不是整数 PCM"};
                        info.bits=le16(fmt.data()+18); tag=WAVE_FORMAT_PCM;
                    }
                    if(tag!=WAVE_FORMAT_PCM||info.channels<1||info.channels>2||info.rate<8000||info.rate>768000||
                      (info.containerBits!=16&&info.containerBits!=24&&info.containerBits!=32)||info.bits<1||info.bits>info.containerBits||le16(fmt.data()+12)!=info.channels*info.containerBits/8)
                        throw Failure{E_NOTIMPL,L"首版 WAV 支持 16/24/32 位整数 PCM、单声道或双声道"};
                    formatFound=true;
                } else if (!memcmp(chunk,"data",4)&&!dataFound) { dataOffset=_ftelli64(file); dataBytes=size; dataFound=true; }
                _fseeki64(file,next,SEEK_SET);
            }
            if(!formatFound||!dataFound) throw Failure{E_FAIL,L"WAV 缺少格式或音频区块"};
            int align=info.channels*info.containerBits/8;
            if(dataBytes%align) throw Failure{E_FAIL,L"WAV 数据没有完整样本帧"};
            info.frames=dataBytes/align; seek(0);
        } catch(...) { fclose(file); file=nullptr; throw; }
    }
    ~WaveSource() override { if(file) fclose(file); }
    size_t read(uint8_t* out,size_t frames) override {
        frames=static_cast<size_t>(std::min<int64_t>(frames,info.frames-current));
        readExact(file,out,frames*info.channels*info.containerBits/8); current+=frames; return frames;
    }
    void seek(int64_t frame) override { current=std::clamp<int64_t>(frame,0,info.frames); if(_fseeki64(file,dataOffset+current*info.channels*info.containerBits/8,SEEK_SET)) throw Failure{E_FAIL,L"无法定位 WAV 样本"}; }
};
class FlacSource final : public Source {
    FLAC__StreamDecoder* decoder=nullptr;
    std::vector<uint8_t> block;
    size_t cursor=0;
    bool decodeError=false;
    static FLAC__StreamDecoderWriteStatus writeCb(const FLAC__StreamDecoder*,const FLAC__Frame* f,const FLAC__int32* const b[],void* user) {
        auto& self=*static_cast<FlacSource*>(user);
        if (f->header.sample_rate!=unsigned(self.info.rate)||f->header.channels!=unsigned(self.info.channels)||f->header.bits_per_sample!=unsigned(self.info.bits)) { self.decodeError=true; return FLAC__STREAM_DECODER_WRITE_STATUS_ABORT; }
        const int bytes=self.info.containerBits/8, shift=self.info.containerBits-self.info.bits;
        self.block.resize(size_t(f->header.blocksize)*self.info.channels*bytes); size_t j=0;
        for(unsigned i=0;i<f->header.blocksize;i++) for(int c=0;c<self.info.channels;c++) {
            uint32_t sample=static_cast<uint32_t>(b[c][i])<<shift;
            for(int k=0;k<bytes;k++) self.block[j++]=static_cast<uint8_t>(sample>>(8*k));
        }
        self.cursor=0; return FLAC__STREAM_DECODER_WRITE_STATUS_CONTINUE;
    }
    static void metaCb(const FLAC__StreamDecoder*,const FLAC__StreamMetadata* m,void* user) {
        if(m->type!=FLAC__METADATA_TYPE_STREAMINFO) return;
        auto& s=*static_cast<FlacSource*>(user); const auto& i=m->data.stream_info;
        s.info={static_cast<int>(i.sample_rate),static_cast<int>(i.bits_per_sample),static_cast<int>(i.channels),i.bits_per_sample<=16?16:i.bits_per_sample<=24?24:32,static_cast<int64_t>(i.total_samples)};
    }
    static void errCb(const FLAC__StreamDecoder*,FLAC__StreamDecoderErrorStatus,void* user) { static_cast<FlacSource*>(user)->decodeError=true; }
public:
    explicit FlacSource(const wchar_t* path) {
        FILE* file=_wfopen(path,L"rb"); if(!file) throw Failure{HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),L"无法读取 FLAC 文件"};
        decoder=FLAC__stream_decoder_new();
        if(!decoder) { fclose(file); throw Failure{E_OUTOFMEMORY,L"无法创建 FLAC 解码器"}; }
        auto init=FLAC__stream_decoder_init_FILE(decoder,file,writeCb,metaCb,errCb,this);
        if(init!=FLAC__STREAM_DECODER_INIT_STATUS_OK) { fclose(file); FLAC__stream_decoder_delete(decoder); decoder=nullptr; throw Failure{E_FAIL,L"无法初始化 FLAC 解码器"}; }
        if(!FLAC__stream_decoder_process_until_end_of_metadata(decoder)||decodeError||info.channels<1||info.channels>2||info.bits<4||info.bits>32||info.frames<=0) {
            FLAC__stream_decoder_finish(decoder); FLAC__stream_decoder_delete(decoder); decoder=nullptr; throw Failure{E_NOTIMPL,L"FLAC 数据无效，或不是单声道/双声道音频"};
        }
    }
    ~FlacSource() override { if(decoder) { FLAC__stream_decoder_finish(decoder); FLAC__stream_decoder_delete(decoder); } }
    size_t read(uint8_t* out,size_t frames) override {
        size_t want=frames*info.channels*info.containerBits/8, written=0;
        while(written<want) {
            if(cursor==block.size()) {
                block.clear(); cursor=0;
                if(FLAC__stream_decoder_get_state(decoder)==FLAC__STREAM_DECODER_END_OF_STREAM) break;
                if(!FLAC__stream_decoder_process_single(decoder)||decodeError) throw Failure{E_FAIL,L"FLAC 解码失败，播放已停止"};
                if(block.empty()) continue;
            }
            size_t n=std::min(want-written,block.size()-cursor); memcpy(out+written,block.data()+cursor,n); written+=n; cursor+=n;
        }
        return written/(info.channels*info.containerBits/8);
    }
    void seek(int64_t frame) override {
        block.clear(); cursor=0; decodeError=false;
        if(!FLAC__stream_decoder_seek_absolute(decoder,static_cast<FLAC__uint64>(frame))) throw Failure{E_FAIL,L"FLAC 样本定位失败"};
    }
};
#include "ffmpeg_source.h"
#include "dop_source.h"
static std::unique_ptr<Source> openSource(const wchar_t* path,int64_t verifiedFrames=0) {
    FILE* f=_wfopen(path,L"rb"); if(!f) throw Failure{HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND),L"文件不存在或无法读取"};
    char magic[4]{}; size_t n=fread(magic,1,4,f); fclose(f);
    if(n!=4) throw Failure{E_FAIL,L"文件过短"};
    if(!memcmp(magic,"fLaC",4)) {try{return std::make_unique<FlacSource>(path);}catch(const Failure& e){if(e.code!=E_NOTIMPL)throw;}}
    if(!memcmp(magic,"RIFF",4)) {try{return std::make_unique<WaveSource>(path);}catch(const Failure& e){if(e.code!=E_NOTIMPL)throw;}}
    return std::make_unique<FFmpegSource>(path,verifiedFrames);
}

#include "read_ahead.h"
#include "sequence_source.h"
#include "processing_source.h"
#include "pcm_formats.h"

static int64_t nextBoundary(const std::vector<int64_t>& boundaries,int64_t position,int64_t limit) {
    int64_t result=limit;
    for(int64_t boundary:boundaries) if(boundary>position&&boundary<result) result=boundary;
    return result;
}

static DWORD silenceOutput(uint8_t* data,size_t bytes,bool muted) {
    if(!muted)return 0;
    memset(data,0,bytes);
    return AUDCLNT_BUFFERFLAGS_SILENT;
}
class Engine {
    std::thread worker;
    std::mutex commandsMutex,statusMutex;
    std::condition_variable signal;
    std::deque<std::function<void()>> commands;
    bool quitting=false,playing=false,draining=false,muted=false;
    Status status{};
    std::wstring errorMessage;
    std::unique_ptr<ReadAheadSource> source;
    ComPtr<IAudioClient> client;
    ComPtr<IAudioRenderClient> render;
    ComPtr<IAudioClock> clock;
    UINT32 bufferFrames=0;
    UINT64 clockFrequency=0,submitted=0;
    int64_t start=0,end=0,base=0;
    int outputBytes=0;
    std::vector<uint8_t> decodeBuffer;
    ProcessingOptions options;
    void close() {
        if(client) client->Stop(); playing=false; draining=false;
        render.Reset(); clock.Reset(); client.Reset(); source.reset();
    }
    void fail(const Failure& e) {
        refreshPosition(); close(); std::lock_guard lock(statusMutex); status.state=4; status.error=e.code; errorMessage=e.message;
    }
    void refreshPosition() {
        if(!clock||!source) return;
        UINT64 ticks=0;
        HRESULT hr=clock->GetPosition(&ticks,nullptr);
        if(SUCCEEDED(hr)&&clockFrequency) {
            auto frames=static_cast<int64_t>((long double)ticks*source->info.rate/clockFrequency);
            std::lock_guard lock(statusMutex); status.position=std::clamp<int64_t>(base+std::min<int64_t>(frames,submitted)-start,0,end-start);
        }
    }
    void pump() {
        if(!playing||!client||!source) return;
        UINT32 padding=0; check(client->GetCurrentPadding(&padding),L"输出设备不可用，播放已停止；请选择设备后继续");
        refreshPosition();
        {std::lock_guard lock(statusMutex);status.bufferedFrames=source->buffered();}
        if(draining) {
            if(padding==0) { check(client->Stop(),L"无法停止音频流"); playing=false; std::lock_guard lock(statusMutex); status.position=end-start; status.state=3; }
            return;
        }
        UINT32 room=bufferFrames-padding;
        UINT32 count=static_cast<UINT32>(std::min<int64_t>(room,end-base-submitted));
        if(!count) { if(end-base-submitted<=0) draining=true; return; }
        int inBytes=source->info.containerBits/8;
        size_t got;
        try {got=source->read(decodeBuffer.data(),count);}
        catch(const Failure& ex) {if(ex.code==AUDCLNT_E_BUFFER_ERROR){std::lock_guard lock(statusMutex);status.underruns++;}throw;}
        if(got!=count) throw Failure{E_FAIL,L"音频提前结束，文件可能不完整"};
        BYTE* out=nullptr; check(render->GetBuffer(count,&out),L"无法获取设备缓冲区");
        if(inBytes==outputBytes) memcpy(out,decodeBuffer.data(),size_t(count)*source->info.channels*inBytes);
        else if(inBytes<outputBytes) {
            // Left-aligned zero padding preserves every significant integer bit.
            size_t samples=size_t(count)*source->info.channels;
            for(size_t i=0;i<samples;i++) { memset(out+i*outputBytes,0,outputBytes-inBytes); memcpy(out+i*outputBytes+outputBytes-inBytes,decodeBuffer.data()+i*inBytes,inBytes); }
        }
        else for(size_t i=0;i<size_t(count)*source->info.channels;i++)memcpy(out+i*outputBytes,decodeBuffer.data()+i*inBytes+inBytes-outputBytes,outputBytes);
        DWORD flags=silenceOutput(out,size_t(count)*source->info.channels*outputBytes,muted);
        check(render->ReleaseBuffer(count,flags),L"设备输出失败，播放已停止"); submitted+=count;
    }
    void loop() {
        HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED); DWORD task=0; HANDLE av=AvSetMmThreadCharacteristicsW(L"Audio",&task);
        while(true) {
            std::function<void()> command;
            { std::unique_lock lock(commandsMutex); if(commands.empty()) signal.wait_for(lock,std::chrono::milliseconds(3),[&]{return quitting||!commands.empty();});
              if(quitting) break; if(!commands.empty()) { command=std::move(commands.front()); commands.pop_front(); } }
            if(command) command();
            try { pump(); } catch(const Failure& e) { fail(e); } catch(...) { fail({E_FAIL,L"音频线程遇到错误，播放已停止"}); }
        }
        close(); if(av) AvRevertMmThreadCharacteristics(av); if(SUCCEEDED(com)) CoUninitialize();
    }
public:
    Engine() { worker=std::thread([this]{loop();}); }
    ~Engine() { {std::lock_guard lock(commandsMutex); quitting=true;} signal.notify_all(); worker.join(); }
    int invoke(std::function<void()> work) {
        auto result=std::make_shared<std::promise<int>>(); auto future=result->get_future();
        { std::lock_guard lock(commandsMutex); commands.push_back([this,work=std::move(work),result]{
            try { work(); result->set_value(0); } catch(const Failure& e) { fail(e); result->set_value(e.code); } catch(...) { fail({E_FAIL,L"音频操作失败"}); result->set_value(E_FAIL); }
        }); } signal.notify_all(); return future.get();
    }
    void open(const wchar_t* path,const wchar_t* deviceId,bool shared,int64_t segmentStart,int64_t segmentEnd) {
        openInput(openSource(path),deviceId,shared,segmentStart,segmentEnd);
    }
    void openSequence(const SourceSegment* segments,int count,const wchar_t* deviceId,bool shared) {
        openInput(std::make_unique<SequenceSource>(segments,count,options.dop!=0),deviceId,shared,0,-1);
    }
    void openInput(std::unique_ptr<Source> input,const wchar_t* deviceId,bool shared,int64_t segmentStart,int64_t segmentEnd) {
        close(); {std::lock_guard lock(statusMutex);status={};}
        bool processing=options.rate||options.bits||options.channels||options.gainDb!=0;
        for(double value:options.eq)processing=processing||value!=0;
        if(processing){input=std::make_unique<RangeSource>(std::move(input),segmentStart,segmentEnd);input=std::make_unique<ProcessingSource>(std::move(input),options);segmentStart=0;segmentEnd=-1;}
        source=std::make_unique<ReadAheadSource>(std::move(input)); start=std::max<int64_t>(0,segmentStart); end=segmentEnd<0?source->info.frames:std::min(segmentEnd,source->info.frames);
        if(start>=end) throw Failure{E_INVALIDARG,L"CUE 片段范围无效"}; base=start; submitted=0; source->seek(start);
        {std::lock_guard lock(statusMutex);status={0,0,source->info.rate,source->info.bits,source->info.containerBits,source->info.channels,shared?1:0,0,end-start};}
        ComPtr<IMMDeviceEnumerator> enumerator; check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),L"无法枚举音频设备");
        ComPtr<IMMDevice> device;
        if(deviceId&&deviceId[0]) check(enumerator->GetDevice(deviceId,&device),L"所选设备已断开；没有切换到其他设备");
        else check(enumerator->GetDefaultAudioEndpoint(eRender,eMultimedia,&device),L"没有可用的输出设备");
        check(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),L"无法打开输出设备");
        AVChannelLayout layout{};av_channel_layout_default(&layout,source->info.channels);DWORD mask=static_cast<DWORD>(layout.u.mask);av_channel_layout_uninit(&layout);
        auto formats=pcmFormats(source->info.rate,source->info.bits,source->info.containerBits,source->info.channels,mask);
        PcmFormat selected=formats.front();
        auto mode=shared?AUDCLNT_SHAREMODE_SHARED:AUDCLNT_SHAREMODE_EXCLUSIVE;
        REFERENCE_TIME period=0,minimum=0; check(client->GetDevicePeriod(&period,&minimum),L"无法读取设备缓冲周期");
        REFERENCE_TIME duration=std::max<REFERENCE_TIME>(period,static_cast<REFERENCE_TIME>(options.bufferMs)*10000);
        DWORD flags=shared?AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM|AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY:0;
        auto initialize=[&](const PcmFormat& format){
            selected=format;client.Reset();check(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),L"无法重新打开输出设备");
            HRESULT result=client->Initialize(mode,flags,duration,shared?0:period,format.data(),nullptr);
            if(result==AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED){
                UINT32 aligned=0;check(client->GetBufferSize(&aligned),L"无法对齐设备缓冲区");auto alignedDuration=static_cast<REFERENCE_TIME>(10000000.0*aligned/source->info.rate+0.5);
                client.Reset();check(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),L"无法重新打开设备");
                result=client->Initialize(mode,flags,alignedDuration,shared?0:period,format.data(),nullptr);
            }
            return result;
        };
        HRESULT init=shared?initialize(selected):negotiatePcm(formats,selected,[&](const PcmFormat& format){return client->IsFormatSupported(mode,format.data(),nullptr);},initialize);
        if(FAILED(init)){
            const wchar_t* reason=init==AUDCLNT_E_DEVICE_IN_USE?L"所选设备被其他程序占用":init==AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED?L"此设备已禁用独占模式":init==AUDCLNT_E_DEVICE_INVALIDATED?L"所选设备已断开或配置已变化":init==AUDCLNT_E_ENDPOINT_CREATE_FAILED?L"驱动无法建立此输出格式，已尝试同精度格式":formatRejected(init)?L"所选设备不接受此歌曲的原采样率/整数格式":L"音频输出建立失败";
            wchar_t message[512];swprintf_s(message,L"%s（0x%08X）；来源 %d Hz / %d-bit / %d 声道，尝试 %d-bit 容器。播放已停止",reason,static_cast<unsigned>(init),source->info.rate,source->info.bits,source->info.channels,selected.value.Format.wBitsPerSample);
            throw Failure{init,message};
        }
        check(client->GetBufferSize(&bufferFrames),L"无法读取设备缓冲区");
        check(client->GetService(IID_PPV_ARGS(&render)),L"无法建立渲染接口"); check(client->GetService(IID_PPV_ARGS(&clock)),L"无法建立播放时钟"); check(clock->GetFrequency(&clockFrequency),L"无法读取播放时钟");
        outputBytes=selected.value.Format.wBitsPerSample/8;
        decodeBuffer.resize(size_t(bufferFrames)*source->info.channels*source->info.containerBits/8);
        size_t prime=static_cast<size_t>(std::min<int64_t>(bufferFrames,end-start));
        if(prime>source->capacityFrames()||!source->waitReady(prime)) throw Failure{E_FAIL,L"输出设备请求的缓冲超出预读能力"};
        std::lock_guard lock(statusMutex); status={1,0,source->info.rate,selected.validBits(),selected.value.Format.wBitsPerSample,source->info.channels,shared?1:0,0,end-start,static_cast<int64_t>(source->buffered()),static_cast<int64_t>(source->capacityFrames()),0,static_cast<int>(bufferFrames)}; errorMessage.clear();
    }
    void configure(ProcessingOptions settings){validateProcessing(settings);options=settings;muted=false;}
    void mute(bool value){if(value&&options.dop)throw Failure{E_INVALIDARG,L"DoP 不允许软件静音，请使用设备硬件静音"};muted=value;}
    void play() {
        if(!client) throw Failure{E_UNEXPECTED,L"请先选择曲目和输出设备"};
        if(playing) return;
        size_t needed=static_cast<size_t>(std::min<int64_t>(bufferFrames,end-base-static_cast<int64_t>(submitted)));
        if(!source->waitReady(needed)) throw Failure{HRESULT_FROM_WIN32(ERROR_TIMEOUT),L"音频预读超时，播放已停止"};
        playing=true; pump(); if(!playing) return;
        check(client->Start(),L"无法启动输出设备"); std::lock_guard lock(statusMutex); status.state=2;
    }
    void stop() { close(); std::lock_guard lock(statusMutex); status={}; errorMessage.clear(); }
    void pause() { if(client) check(client->Stop(),L"设备已不可用"); refreshPosition(); playing=false; std::lock_guard lock(statusMutex); if(status.state==2) status.state=1; }
    void seek(int64_t relative) {
        if(!client||!source) throw Failure{E_UNEXPECTED,L"当前没有可定位的音频"};
        bool resume=playing; check(client->Stop(),L"无法暂停输出设备"); playing=false; check(client->Reset(),L"无法清空输出缓冲区");
        base=start+std::clamp<int64_t>(relative,0,end-start-1); source->seek(base); submitted=0; draining=false;
        {std::lock_guard lock(statusMutex); status.position=base-start; status.state=1;}
        if(resume) play();
    }
    void trim(const std::vector<int64_t>& boundaries) {
        if(!client||!source) return;
        refreshPosition();
        int64_t position;{std::lock_guard lock(statusMutex);position=start+status.position;}
        if(nextBoundary(boundaries,position,end)==end) return;
        bool resume=playing;
        check(client->Stop(),L"无法暂停输出设备"); playing=false;
        refreshPosition();
        {std::lock_guard lock(statusMutex);position=start+status.position;}
        int64_t limit=nextBoundary(boundaries,position,end);
        check(client->Reset(),L"无法撤销后续音频缓冲");
        end=limit; base=position; submitted=0; draining=false;
        if(base<end) source->seek(base);
        {std::lock_guard lock(statusMutex);status.duration=end-start;status.position=base-start;status.state=base<end?1:3;}
        if(resume&&base<end) play();
    }
    Status snapshot() {std::lock_guard lock(statusMutex); return status;}
    std::wstring error() {std::lock_guard lock(statusMutex); return errorMessage;}
};
API void* __cdecl muyu_create() {try{return new Engine();}catch(...){return nullptr;}}
API void __cdecl muyu_destroy(void* p) {delete static_cast<Engine*>(p);}
API int __cdecl muyu_open(void* p,const wchar_t* path,const wchar_t* device,int shared,int64_t start,int64_t end) {auto* e=static_cast<Engine*>(p); return e->invoke([=]{e->open(path,device,shared!=0,start,end);});}
API int __cdecl muyu_open_sequence(void* p,const SourceSegment* segments,int count,const wchar_t* device,int shared) {
    if(!p||!segments||count<1||count>100000)return E_INVALIDARG;
    auto* e=static_cast<Engine*>(p);return e->invoke([=]{e->openSequence(segments,count,device,shared!=0);});
}
API int __cdecl muyu_configure(void* p,ProcessingOptions settings){if(!p)return E_POINTER;auto* e=static_cast<Engine*>(p);return e->invoke([=]{e->configure(settings);});}
API int __cdecl muyu_play(void* p) {auto* e=static_cast<Engine*>(p); return e->invoke([e]{e->play();});}
API int __cdecl muyu_pause(void* p) {auto* e=static_cast<Engine*>(p); return e->invoke([e]{e->pause();});}
API int __cdecl muyu_mute(void* p,int muted) {if(!p)return E_POINTER;if(muted!=0&&muted!=1)return E_INVALIDARG;auto* e=static_cast<Engine*>(p);return e->invoke([=]{e->mute(muted!=0);});}
API int __cdecl muyu_stop(void* p) {auto* e=static_cast<Engine*>(p); return e->invoke([e]{e->stop();});}
API int __cdecl muyu_seek(void* p,int64_t frame) {auto* e=static_cast<Engine*>(p); return e->invoke([=]{e->seek(frame);});}
API int __cdecl muyu_trim(void* p,const int64_t* ends,int count) {
    if(!p||!ends||count<1||count>100000)return E_INVALIDARG;
    auto* e=static_cast<Engine*>(p);std::vector<int64_t> boundaries(ends,ends+count);
    return e->invoke([e,boundaries=std::move(boundaries)]{e->trim(boundaries);});
}
API int __cdecl muyu_status(void* p,Status* result) {if(!p||!result)return E_POINTER; *result=static_cast<Engine*>(p)->snapshot(); return 0;}
API int __cdecl muyu_error(void* p,wchar_t* text,int capacity) {if(!p||!text||capacity<=0)return E_POINTER; wcsncpy_s(text,capacity,static_cast<Engine*>(p)->error().c_str(),_TRUNCATE); return 0;}
API int __cdecl muyu_probe(const wchar_t* path,SourceInfo* info) {try{auto s=openSource(path);*info=s->info;return 0;}catch(const Failure& e){return e.code;}catch(...){return E_FAIL;}}
API int __cdecl muyu_read_pcm(const wchar_t* path,int64_t start,int frames,uint8_t* out,int capacity) {try{if(!path||!out||frames<0||capacity<0)return E_INVALIDARG;auto s=openSource(path); int64_t bytes=static_cast<int64_t>(frames)*s->info.channels*s->info.containerBits/8; if(bytes>capacity||start<0||start>=s->info.frames)return E_INVALIDARG; s->seek(start); return static_cast<int>(s->read(out,frames));}catch(...){return -1;}}
API void* __cdecl muyu_source_create(const SourceSegment* segments,int count,ProcessingOptions options,SourceInfo* result){try{if(!result)throw Failure{E_POINTER,L"音源信息为空"};validateProcessing(options);auto input=std::make_unique<SequenceSource>(segments,count,options.dop!=0);std::unique_ptr<Source> source=std::move(input);bool processing=options.rate||options.bits||options.channels||options.gainDb!=0;for(double value:options.eq)processing=processing||value!=0;if(processing)source=std::make_unique<ProcessingSource>(std::move(source),options);auto ring=std::make_unique<ReadAheadSource>(std::move(source));ring->seek(0);*result=ring->info;return ring.release();}catch(...){return nullptr;}}
API void __cdecl muyu_source_destroy(void* source){delete static_cast<ReadAheadSource*>(source);}
API int __cdecl muyu_source_read(void* source,uint8_t* out,int frames,int capacity){try{auto* ring=static_cast<ReadAheadSource*>(source);if(!ring||!out||frames<0||static_cast<int64_t>(frames)*ring->info.channels*ring->info.containerBits/8>capacity)return -1;return static_cast<int>(ring->read(out,frames));}catch(...){return -1;}}
API int __cdecl muyu_source_wait(void* source,int frames){try{return static_cast<ReadAheadSource*>(source)->waitReady(frames)?0:-1;}catch(...){return -1;}}
API int __cdecl muyu_source_seek(void* source,int64_t frame){try{static_cast<ReadAheadSource*>(source)->seek(frame);return 0;}catch(...){return -1;}}
API int __cdecl muyu_devices(DeviceInfo* out,int capacity) {
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED); int result=0;
    try {
        ComPtr<IMMDeviceEnumerator> e; check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&e)),L"");
        ComPtr<IMMDevice> def; std::wstring defaultId;
        if(SUCCEEDED(e->GetDefaultAudioEndpoint(eRender,eMultimedia,&def))) {LPWSTR id=nullptr;if(SUCCEEDED(def->GetId(&id))){defaultId=id;CoTaskMemFree(id);}}
        ComPtr<IMMDeviceCollection> collection; check(e->EnumAudioEndpoints(eRender,DEVICE_STATEMASK_ALL,&collection),L""); UINT count=0; collection->GetCount(&count);
        for(UINT i=0;i<count&&result<capacity;i++) {
            ComPtr<IMMDevice> device; if(FAILED(collection->Item(i,&device)))continue; LPWSTR id=nullptr; if(FAILED(device->GetId(&id)))continue;
            DeviceInfo item{}; wcsncpy_s(item.id,id,_TRUNCATE); item.isDefault=defaultId==id; CoTaskMemFree(id);
            DWORD state=0;device->GetState(&state);item.state=static_cast<int>(state);
            ComPtr<IPropertyStore> props; PROPVARIANT value; PropVariantInit(&value);
            if(SUCCEEDED(device->OpenPropertyStore(STGM_READ,&props))&&SUCCEEDED(props->GetValue(PKEY_Device_FriendlyName,&value))&&value.vt==VT_LPWSTR)wcsncpy_s(item.name,value.pwszVal,_TRUNCATE);
            PropVariantClear(&value);if(props&&SUCCEEDED(props->GetValue(PKEY_DeviceInterface_FriendlyName,&value))&&value.vt==VT_LPWSTR)wcsncpy_s(item.interfaceName,value.pwszVal,_TRUNCATE);PropVariantClear(&value);out[result++]=item;
        }
    }catch(...){result=-1;}
    if(SUCCEEDED(com))CoUninitialize(); return result;
}
struct SupportedFormat{int rate,bits,channels;};
API int __cdecl muyu_formats(const wchar_t* id,SupportedFormat* output,int capacity){
    HRESULT com=CoInitializeEx(nullptr,COINIT_MULTITHREADED);int count=0;
    try{ComPtr<IMMDeviceEnumerator> enumerator;check(CoCreateInstance(__uuidof(MMDeviceEnumerator),nullptr,CLSCTX_ALL,IID_PPV_ARGS(&enumerator)),L"");ComPtr<IMMDevice> device;check(enumerator->GetDevice(id,&device),L"");ComPtr<IAudioClient> client;check(device->Activate(__uuidof(IAudioClient),CLSCTX_ALL,nullptr,&client),L"");
        const int rates[]{44100,48000,88200,96000,176400,192000,352800,384000,705600,768000};
        for(int rate:rates)for(int bits:{16,24,32})for(int channels:{1,2,4,6,8}){AVChannelLayout layout{};av_channel_layout_default(&layout,channels);auto formats=pcmFormats(rate,bits,bits,channels,static_cast<DWORD>(layout.u.mask));av_channel_layout_uninit(&layout);if(std::any_of(formats.begin(),formats.end(),[&](const auto& format){return client->IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE,format.data(),nullptr)==S_OK;})&&count<capacity)output[count++]={rate,bits,channels};}
    }catch(...){count=-1;}if(SUCCEEDED(com))CoUninitialize();return count;
}
