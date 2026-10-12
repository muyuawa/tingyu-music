#pragma once
#include <cmath>
struct ProcessingOptions {int rate=0,bits=0,channels=0,bufferMs=100;double gainDb=0;double eq[10]{};int dop=0;};
static void validateProcessing(const ProcessingOptions& s){
    if((s.rate&&(s.rate<8000||s.rate>768000))||(s.bits&&s.bits!=16&&s.bits!=24&&s.bits!=32)||(s.channels&&s.channels!=1&&s.channels!=2)||s.bufferMs<10||s.bufferMs>1000||!std::isfinite(s.gainDb)||s.gainDb<-100||s.gainDb>30||(s.dop!=0&&s.dop!=1))throw Failure{E_INVALIDARG,L"输出设置无效"};
    bool modified=s.rate||s.bits||s.channels||s.gainDb!=0;for(double gain:s.eq){if(!std::isfinite(gain)||gain<-12||gain>12)throw Failure{E_INVALIDARG,L"均衡器设置无效"};modified=modified||gain!=0;}if(s.dop&&modified)throw Failure{E_INVALIDARG,L"DoP 不能应用 PCM 处理"};
}
class RangeSource final : public Source {
    std::unique_ptr<Source> source;int64_t begin,position=0;
public:
    RangeSource(std::unique_ptr<Source> input,int64_t start,int64_t end):source(std::move(input)),begin(start){info=source->info;int64_t limit=end<0?info.frames:end;if(start<0||start>=limit||limit>info.frames)throw Failure{E_INVALIDARG,L"音源范围无效"};info.frames=limit-start;source->seek(start);}
    void seek(int64_t frame)override{if(frame<0||frame>=info.frames)throw Failure{E_INVALIDARG,L"音源定位越界"};source->seek(begin+frame);position=frame;}
    size_t read(uint8_t* out,size_t count)override{size_t got=source->read(out,static_cast<size_t>(std::min<int64_t>(count,info.frames-position)));position+=got;return got;}
};
class ProcessingSource final : public Source {
    struct Biquad {double b0=1,b1=0,b2=0,a1=0,a2=0,z1=0,z2=0;double tick(double x){double y=b0*x+z1;z1=b1*x-a1*y+z2;z2=b2*x-a2*y;return y;}};
    std::unique_ptr<Source> input;SwrContext* converter=nullptr;ProcessingOptions options;
    std::vector<uint8_t> raw;std::vector<int32_t> expanded,output;size_t cursor=0,available=0;
    std::vector<std::vector<Biquad>> filters;double gain;bool ended=false,dither=false;int64_t position=0;uint32_t noise=0x13579bdf;
    double random(){noise^=noise<<13;noise^=noise>>17;noise^=noise<<5;return double(noise)/4294967296.0;}
    bool fill(){
        size_t got=ended?0:input->read(raw.data(),4096);if(got<4096)ended=true;
        int bytes=input->info.containerBits/8;
        for(size_t sample=0;sample<got*input->info.channels;sample++){uint32_t word=0;for(int i=0;i<bytes;i++)word|=uint32_t(raw[sample*bytes+i])<<(8*i);expanded[sample]=static_cast<int32_t>(word<<(32-input->info.containerBits));}
        const uint8_t* in=reinterpret_cast<const uint8_t*>(expanded.data());uint8_t* out=reinterpret_cast<uint8_t*>(output.data());
        int count=swr_convert(converter,&out,static_cast<int>(output.size()/info.channels),got?&in:nullptr,static_cast<int>(got));
        if(count<0)throw Failure{E_FAIL,L"重采样或声道转换失败"};cursor=0;available=static_cast<size_t>(count);
        for(size_t sample=0;sample<available*info.channels;sample++){
            double value=output[sample]/2147483648.0*gain;for(auto& filter:filters[sample%info.channels])value=filter.tick(value);
            if(dither)value+=(random()-random())/std::ldexp(1.0,info.bits-1);
            double scaled=std::clamp(value,-1.0,std::nextafter(1.0,0.0))*2147483648.0;
            int64_t quantized=std::llround(scaled/std::ldexp(1.0,32-info.bits));
            quantized=std::clamp<int64_t>(quantized,-(int64_t(1)<<(info.bits-1)),(int64_t(1)<<(info.bits-1))-1);
            output[sample]=static_cast<int32_t>(quantized* (int64_t(1)<<(32-info.bits)));
        }
        return available>0;
    }
public:
    ProcessingSource(std::unique_ptr<Source> source,const ProcessingOptions& settings):input(std::move(source)),options(settings),gain(std::pow(10.0,settings.gainDb/20.0)){
        info=input->info;info.rate=settings.rate?settings.rate:info.rate;info.bits=settings.bits?settings.bits:info.bits;info.channels=settings.channels?settings.channels:info.channels;info.containerBits=32;
        dither=info.bits<32&&(info.bits<input->info.bits||info.rate!=input->info.rate||info.channels!=input->info.channels||settings.gainDb!=0||std::any_of(std::begin(settings.eq),std::end(settings.eq),[](double gain){return gain!=0;}));
        info.frames=av_rescale_rnd(input->info.frames,info.rate,input->info.rate,AV_ROUND_NEAR_INF);
        AVChannelLayout in{},out{};av_channel_layout_default(&in,input->info.channels);av_channel_layout_default(&out,info.channels);
        int result=swr_alloc_set_opts2(&converter,&out,AV_SAMPLE_FMT_S32,info.rate,&in,AV_SAMPLE_FMT_S32,input->info.rate,0,nullptr);av_channel_layout_uninit(&in);av_channel_layout_uninit(&out);
        if(result<0)throw Failure{E_FAIL,L"无法建立采样转换"};
        av_opt_set_int(converter,"filter_size",64,0);av_opt_set_int(converter,"phase_shift",10,0);av_opt_set_double(converter,"cutoff",0.97,0);
        if(swr_init(converter)<0){swr_free(&converter);throw Failure{E_FAIL,L"无法初始化高质量采样转换"};}
        raw.resize(4096*input->info.channels*input->info.containerBits/8);expanded.resize(4096*input->info.channels);
        output.resize(static_cast<size_t>(av_rescale_rnd(4096,info.rate,input->info.rate,AV_ROUND_UP)+512)*info.channels);filters.resize(info.channels);
        const double frequencies[]{31,62,125,250,500,1000,2000,4000,8000,16000};
        for(auto& channel:filters)for(int i=0;i<10;i++)if(settings.eq[i]!=0&&frequencies[i]<info.rate*.45){double A=std::pow(10.0,settings.eq[i]/40),omega=2*3.141592653589793*frequencies[i]/info.rate,alpha=std::sin(omega)/(2*1.4),a0=1+alpha/A;channel.push_back({(1+alpha*A)/a0,-2*std::cos(omega)/a0,(1-alpha*A)/a0,-2*std::cos(omega)/a0,(1-alpha/A)/a0});}
    }
    ~ProcessingSource()override{swr_free(&converter);}
    void seek(int64_t frame)override{input->seek(std::min(input->info.frames-1,av_rescale(frame,input->info.rate,info.rate)));swr_close(converter);if(swr_init(converter)<0)throw Failure{E_FAIL,L"无法重置采样转换"};cursor=available=0;ended=false;position=frame;for(auto& channel:filters)for(auto& f:channel)f.z1=f.z2=0;}
    size_t read(uint8_t* out,size_t frames)override{frames=static_cast<size_t>(std::min<int64_t>(frames,info.frames-position));size_t written=0;while(written<frames){if(cursor==available&&!fill())break;size_t count=std::min(frames-written,available-cursor);memcpy(out+written*info.channels*4,output.data()+cursor*info.channels,count*info.channels*4);cursor+=count;written+=count;}position+=written;return written;}
};
