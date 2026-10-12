#pragma once
// DoP 1.1: oldest DSD bit occupies bit 15, markers alternate 05/FA.
// Only uncompressed DSD packets are accepted; DST must use explicit PCM decode.
class DopSource final : public Source {
    FILE* file=nullptr;AVIOContext* io=nullptr;AVFormatContext* format=nullptr;AVPacket* packet=nullptr;
    int stream=-1;bool planar=false,lsb=false;std::vector<uint8_t> block,older;size_t cursor=0;bool pending=false;int64_t produced=0;
    static int readFile(void* p,uint8_t* out,int count){size_t got=fread(out,1,count,static_cast<FILE*>(p));return got?static_cast<int>(got):AVERROR_EOF;}
    static int64_t seekFile(void* p,int64_t offset,int whence){auto* f=static_cast<FILE*>(p);if(whence&AVSEEK_SIZE){auto pos=_ftelli64(f);_fseeki64(f,0,SEEK_END);auto size=_ftelli64(f);_fseeki64(f,pos,SEEK_SET);return size;}return _fseeki64(f,offset,whence&~AVSEEK_FORCE)?AVERROR(EIO):_ftelli64(f);}
    static uint8_t reverse(uint8_t b){b=static_cast<uint8_t>((b>>4)|(b<<4));b=static_cast<uint8_t>(((b&0xcc)>>2)|((b&0x33)<<2));return static_cast<uint8_t>(((b&0xaa)>>1)|((b&0x55)<<1));}
    void release(){av_packet_free(&packet);avformat_close_input(&format);if(io){av_freep(&io->buffer);avio_context_free(&io);}if(file){fclose(file);file=nullptr;}}
    void reset(){
        avformat_close_input(&format);if(avio_seek(io,0,SEEK_SET)<0)throw Failure{E_FAIL,L"DSD 文件无法定位"};format=avformat_alloc_context();if(!format)throw Failure{E_OUTOFMEMORY,L"DSD 内存不足"};format->pb=io;format->flags|=AVFMT_FLAG_CUSTOM_IO;AVDictionary* options=nullptr;av_dict_set(&options,"protocol_whitelist","file",0);int result=avformat_open_input(&format,nullptr,nullptr,&options);av_dict_free(&options);if(result<0)throw Failure{E_FAIL,L"无法读取 DSD 容器"};stream=av_find_best_stream(format,AVMEDIA_TYPE_AUDIO,-1,-1,nullptr,0);if(stream<0)throw Failure{E_FAIL,L"DSD 音频流无效"};
    }
    bool next(){
        block.clear();cursor=0;
        for(;;){int result;while((result=av_read_frame(format,packet))>=0&&packet->stream_index!=stream)av_packet_unref(packet);if(result==AVERROR_EOF)return false;if(result<0||packet->size%info.channels)throw Failure{E_FAIL,L"DSD 数据包不完整"};
            int bytesPerChannel=packet->size/info.channels;
            for(int sample=0;sample<bytesPerChannel;sample++){
                for(int c=0;c<info.channels;c++){uint8_t value=packet->data[planar?c*bytesPerChannel+sample:sample*info.channels+c];if(lsb)value=reverse(value);if(!pending)older[c]=value;else{block.push_back(value);block.push_back(older[c]);block.push_back(produced%2?0xfa:0x05);}}
                if(pending)produced++;pending=!pending;
            }
            av_packet_unref(packet);if(!block.empty())return true;
        }
    }
public:
    explicit DopSource(const wchar_t* path,int64_t verifiedFrames=0){
        try{file=_wfopen(path,L"rb");if(!file)throw Failure{E_FAIL,L"无法读取 DSD 文件"};auto* buffer=static_cast<uint8_t*>(av_malloc(65536));if(!buffer)throw Failure{E_OUTOFMEMORY,L"DSD 内存不足"};io=avio_alloc_context(buffer,65536,0,file,readFile,nullptr,seekFile);if(!io){av_free(buffer);throw Failure{E_OUTOFMEMORY,L"DSD 内存不足"};}reset();auto* parameters=format->streams[stream]->codecpar;
            switch(parameters->codec_id){case AV_CODEC_ID_DSD_LSBF_PLANAR:planar=true;lsb=true;break;case AV_CODEC_ID_DSD_MSBF_PLANAR:planar=true;break;case AV_CODEC_ID_DSD_LSBF:lsb=true;break;case AV_CODEC_ID_DSD_MSBF:break;default:throw Failure{E_NOTIMPL,L"DoP 只接受未压缩 DSF/DFF；未自动转成 PCM"};}
            if(parameters->sample_rate%2||parameters->ch_layout.nb_channels<1||parameters->ch_layout.nb_channels>2)throw Failure{E_NOTIMPL,L"DoP 需要有效的单声道或双声道 DSD"};info={parameters->sample_rate/2,24,parameters->ch_layout.nb_channels,24,verifiedFrames};older.resize(info.channels);packet=av_packet_alloc();if(!packet)throw Failure{E_OUTOFMEMORY,L"DSD 内存不足"};if(!verifiedFrames)while(next())info.frames+=static_cast<int64_t>(block.size()/(info.channels*3));if(pending)throw Failure{E_INVALIDARG,L"DSD 结尾不足完整 DoP 帧，请明确改用 PCM 解码"};if(info.frames<=0)throw Failure{E_FAIL,L"DSD 内容为空"};seek(0);
        }catch(...){release();throw;}
    }
    ~DopSource()override{release();}
    size_t read(uint8_t* out,size_t frames)override{size_t wanted=frames*info.channels*3,written=0;while(written<wanted){if(cursor==block.size()&&!next())break;size_t count=std::min(wanted-written,block.size()-cursor);memcpy(out+written,block.data()+cursor,count);written+=count;cursor+=count;}return written/(info.channels*3);}
    void seek(int64_t target)override{if(target<0||target>=info.frames)throw Failure{E_INVALIDARG,L"DoP 定位越界"};reset();produced=0;pending=false;block.clear();cursor=0;size_t remaining=static_cast<size_t>(target)*info.channels*3;while(remaining){if(!next())throw Failure{E_FAIL,L"DSD 文件提前结束"};size_t count=std::min(remaining,block.size());cursor=count;remaining-=count;}}
};
