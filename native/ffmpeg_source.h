#pragma once
#include <io.h>
#include <share.h>
#include <unordered_map>
extern "C" {
#include <libavformat/avformat.h>
#include <libavcodec/avcodec.h>
#include <libavcodec/codec_desc.h>
#include <libavutil/channel_layout.h>
#include <libavutil/opt.h>
#include <libswresample/swresample.h>
}

// Custom local-file IO and a protocol whitelist keep decoding entirely offline.
// Exact lengths and seeks use decoded samples, never compressed byte estimates.
class FFmpegSource final : public Source {
    FILE* file=nullptr;
    AVIOContext* io=nullptr;
    AVFormatContext* format=nullptr;
    AVCodecContext* codec=nullptr;
    SwrContext* converter=nullptr;
    AVPacket* packet=nullptr;
    AVFrame* frame=nullptr;
    int stream=-1;
    bool inputEnded=false, sentEnd=false;
    std::vector<uint8_t> block;
    size_t cursor=0;
    std::wstring path;
    static int readFile(void* user,uint8_t* data,int count) {
        size_t got=fread(data,1,count,static_cast<FILE*>(user));
        return got?static_cast<int>(got):ferror(static_cast<FILE*>(user))?AVERROR(EIO):AVERROR_EOF;
    }
    static int64_t seekFile(void* user,int64_t offset,int whence) {
        auto* f=static_cast<FILE*>(user);
        if(whence&AVSEEK_SIZE) {auto pos=_ftelli64(f);_fseeki64(f,0,SEEK_END);auto size=_ftelli64(f);_fseeki64(f,pos,SEEK_SET);return size;}
        return _fseeki64(f,offset,whence&~AVSEEK_FORCE)?AVERROR(EIO):_ftelli64(f);
    }
    void release() {
        av_frame_free(&frame);av_packet_free(&packet);swr_free(&converter);avcodec_free_context(&codec);
        avformat_close_input(&format);
        if(io){av_freep(&io->buffer);avio_context_free(&io);}
        if(file){fclose(file);file=nullptr;}
    }
    bool nextBlock() {
        block.clear();cursor=0;
        for(;;) {
            int result=avcodec_receive_frame(codec,frame);
            if(result==0) {
                int count=frame->nb_samples;
                block.resize(size_t(count)*info.channels*4);
                uint8_t* output=block.data();
                int got=swr_convert(converter,&output,count,const_cast<const uint8_t**>(frame->extended_data),count);
                if(got<0)throw Failure{E_FAIL,L"音频样本格式转换失败"};
                block.resize(size_t(got)*info.channels*4);av_frame_unref(frame);
                if(got)return true;
                continue;
            }
            if(result==AVERROR_EOF)return false;
            if(result!=AVERROR(EAGAIN))throw Failure{E_FAIL,L"音频解码失败，文件可能已损坏"};
            if(inputEnded) {
                if(sentEnd)return false;
                if(avcodec_send_packet(codec,nullptr)<0)throw Failure{E_FAIL,L"无法结束音频解码"};
                sentEnd=true;continue;
            }
            while((result=av_read_frame(format,packet))>=0&&packet->stream_index!=stream)av_packet_unref(packet);
            if(result==AVERROR_EOF){inputEnded=true;continue;}
            if(result<0)throw Failure{E_FAIL,L"读取音频包失败"};
            result=avcodec_send_packet(codec,packet);av_packet_unref(packet);
            if(result<0)throw Failure{E_FAIL,L"解码器拒绝音频包"};
        }
    }
    void rewind() {
        const auto expected=info;release();initialize();
        if(info.rate!=expected.rate||info.bits!=expected.bits||info.channels!=expected.channels)throw Failure{E_FAIL,L"定位时音源格式已变化"};
        info.frames=expected.frames;
        inputEnded=sentEnd=false;block.clear();cursor=0;
    }
    void initialize() {
            file=_wfsopen(path.c_str(),L"rb",_SH_DENYWR);if(!file)throw Failure{E_FAIL,L"无法读取音源"};
            auto* buffer=static_cast<uint8_t*>(av_malloc(65536));
            if(!buffer)throw Failure{E_OUTOFMEMORY,L"解码缓冲分配失败"};
            io=avio_alloc_context(buffer,65536,0,file,readFile,nullptr,seekFile);
            if(!io){av_free(buffer);throw Failure{E_OUTOFMEMORY,L"无法创建本地文件读取器"};}
            format=avformat_alloc_context();if(!format)throw Failure{E_OUTOFMEMORY,L"无法创建解码器"};
            format->pb=io;format->flags|=AVFMT_FLAG_CUSTOM_IO;
            AVDictionary* options=nullptr;av_dict_set(&options,"protocol_whitelist","file",0);
            int result=avformat_open_input(&format,nullptr,nullptr,&options);av_dict_free(&options);
            if(result<0||avformat_find_stream_info(format,nullptr)<0)throw Failure{E_FAIL,L"无法识别本地音频格式"};
            const AVCodec* decoder=nullptr;stream=av_find_best_stream(format,AVMEDIA_TYPE_AUDIO,-1,-1,&decoder,0);
            if(stream<0)throw Failure{E_FAIL,L"文件中没有音频流"};
            codec=avcodec_alloc_context3(decoder);if(!codec)throw Failure{E_OUTOFMEMORY,L"无法创建音频解码器"};
            if(avcodec_parameters_to_context(codec,format->streams[stream]->codecpar)<0||avcodec_open2(codec,decoder,nullptr)<0)
                throw Failure{E_FAIL,L"此音频编码无法解码"};
            if(codec->sample_rate<8000||codec->sample_rate>768000||codec->ch_layout.nb_channels<1||codec->ch_layout.nb_channels>8)
                throw Failure{E_NOTIMPL,L"音频采样率或声道数超出支持范围"};
            int bits=codec->bits_per_raw_sample;
            if(bits<=0)bits=av_get_exact_bits_per_sample(codec->codec_id);
            if(bits<=0)bits=32; // Lossy/float decoders have no integer bit-perfect claim.
            info={codec->sample_rate,std::min(32,bits),codec->ch_layout.nb_channels,32,0};
            const auto* description=avcodec_descriptor_get(codec->codec_id);
            info.encoding=codec->codec_id==AV_CODEC_ID_PCM_F32LE||codec->codec_id==AV_CODEC_ID_PCM_F32BE||codec->codec_id==AV_CODEC_ID_PCM_F64LE||codec->codec_id==AV_CODEC_ID_PCM_F64BE?1:
                codec->codec_id==AV_CODEC_ID_DSD_LSBF||codec->codec_id==AV_CODEC_ID_DSD_LSBF_PLANAR||codec->codec_id==AV_CODEC_ID_DSD_MSBF||codec->codec_id==AV_CODEC_ID_DSD_MSBF_PLANAR?3:description&&(description->props&AV_CODEC_PROP_LOSSLESS)?0:2;
            if(swr_alloc_set_opts2(&converter,&codec->ch_layout,AV_SAMPLE_FMT_S32,info.rate,&codec->ch_layout,codec->sample_fmt,info.rate,0,nullptr)<0||swr_init(converter)<0)
                throw Failure{E_FAIL,L"无法配置解码样本格式"};
            packet=av_packet_alloc();frame=av_frame_alloc();if(!packet||!frame)throw Failure{E_OUTOFMEMORY,L"无法分配解码数据"};
    }
public:
    explicit FFmpegSource(const wchar_t* filename,int64_t verifiedFrames=0):path(filename) {
        try {
            initialize();
            BY_HANDLE_FILE_INFORMATION identity{};if(!GetFileInformationByHandle(reinterpret_cast<HANDLE>(_get_osfhandle(_fileno(file))),&identity))throw Failure{E_FAIL,L"无法读取音源身份"};
            std::string key=std::to_string(identity.dwVolumeSerialNumber)+"/"+std::to_string(identity.nFileIndexHigh)+"/"+std::to_string(identity.nFileIndexLow)+"/"+std::to_string(identity.nFileSizeHigh)+"/"+std::to_string(identity.nFileSizeLow)+"/"+std::to_string(identity.ftLastWriteTime.dwHighDateTime)+"/"+std::to_string(identity.ftLastWriteTime.dwLowDateTime);
            static std::mutex cacheMutex;static std::unordered_map<std::string,SourceInfo> cache;
            info.frames=verifiedFrames;
            if(!info.frames){std::lock_guard lock(cacheMutex);auto found=cache.find(key);if(found!=cache.end()&&found->second.rate==info.rate&&found->second.bits==info.bits&&found->second.channels==info.channels)info.frames=found->second.frames;}
            if(!info.frames){while(nextBlock())info.frames+=static_cast<int64_t>(block.size()/(info.channels*4));std::lock_guard lock(cacheMutex);if(cache.size()>2048)cache.clear();cache[key]=info;}
            if(info.frames<=0)throw Failure{E_FAIL,L"音频内容为空"};
            rewind();
        } catch(...){release();throw;}
    }
    ~FFmpegSource() override {release();}
    size_t read(uint8_t* out,size_t frames) override {
        size_t want=frames*info.channels*4,written=0;
        while(written<want){if(cursor==block.size()&&!nextBlock())break;size_t count=std::min(want-written,block.size()-cursor);memcpy(out+written,block.data()+cursor,count);cursor+=count;written+=count;}
        return written/(info.channels*4);
    }
    void seek(int64_t target) override {
        if(target<0||target>info.frames)throw Failure{E_INVALIDARG,L"样本位置越界"};
        rewind();size_t remaining=static_cast<size_t>(target)*info.channels*4;
        while(remaining){if(!nextBlock())throw Failure{E_FAIL,L"音源提前结束"};size_t count=std::min(remaining,block.size());cursor=count;remaining-=count;}
    }
};
