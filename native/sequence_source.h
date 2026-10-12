#pragma once
struct SourceSegment {
    const wchar_t* path; int64_t start,end; int rate=0,bits=0,channels=0,encoding=0;int64_t total=0;
    const wchar_t* identity=nullptr;const wchar_t* sha256=nullptr;int64_t length=0;
};
class SequenceSource final : public Source {
    struct Handle {HANDLE value=INVALID_HANDLE_VALUE;~Handle(){if(value!=INVALID_HANDLE_VALUE)CloseHandle(value);}Handle()=default;Handle(const Handle&)=delete;Handle(Handle&& other)noexcept:value(other.value){other.value=INVALID_HANDLE_VALUE;}Handle& operator=(Handle&& other)noexcept{if(this!=&other){if(value!=INVALID_HANDLE_VALUE)CloseHandle(value);value=other.value;other.value=INVALID_HANDLE_VALUE;}return *this;}};
    struct Segment {std::wstring path,identity,sha;int64_t length,start,end,offset;SourceInfo format;bool verified;};
    struct Prepared {Handle guard;std::unique_ptr<Source> source;};
    std::vector<Segment> segments;
    Prepared current;
    size_t index=0,futureIndex=SIZE_MAX;
    int64_t position=0;
    std::vector<uint8_t> scratch;
    bool dop=false;
    std::future<Prepared> future;
    std::shared_ptr<std::atomic_bool> cancelled;
    static Prepared prepare(const Segment& segment,bool dop,const std::shared_ptr<std::atomic_bool>& cancel){
        Prepared result;
        if(segment.verified){
            result.guard.value=CreateFileW(segment.path.c_str(),GENERIC_READ,FILE_SHARE_READ,nullptr,OPEN_EXISTING,FILE_FLAG_SEQUENTIAL_SCAN,nullptr);
            if(result.guard.value==INVALID_HANDLE_VALUE)throw Failure{E_FAIL,L"音源不存在、不可读或正在被修改"};
            BY_HANDLE_FILE_INFORMATION info{};check(GetFileInformationByHandle(result.guard.value,&info)?S_OK:E_FAIL,L"无法验证音源身份");
            wchar_t id[40];swprintf_s(id,L"%08X:%08X%08X",info.dwVolumeSerialNumber,info.nFileIndexHigh,info.nFileIndexLow);
            if(segment.identity!=id||segment.length!=((int64_t(info.nFileSizeHigh)<<32)|info.nFileSizeLow))throw Failure{E_FAIL,L"音源身份已变化，请重新导入或重新定位"};
            BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;
            try{
                if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0||BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)<0)throw Failure{E_FAIL,L"无法建立音源摘要验证"};
                std::vector<uint8_t> buffer(65536);DWORD got=0;
                for(;;){if(cancel&&cancel->load())throw Failure{HRESULT_FROM_WIN32(ERROR_CANCELLED),L"音源准备已取消"};if(!ReadFile(result.guard.value,buffer.data(),static_cast<DWORD>(buffer.size()),&got,nullptr))throw Failure{E_FAIL,L"读取音源摘要失败"};if(!got)break;if(BCryptHashData(hash,buffer.data(),got,0)<0)throw Failure{E_FAIL,L"音源摘要验证失败"};}
                uint8_t digest[32];if(BCryptFinishHash(hash,digest,32,0)<0)throw Failure{E_FAIL,L"音源摘要验证失败"};std::wstring text;const wchar_t* hex=L"0123456789ABCDEF";for(uint8_t b:digest){text+=hex[b>>4];text+=hex[b&15];}
                if(text!=segment.sha)throw Failure{E_FAIL,L"音源内容已变化，请重新导入或重新定位"};
                BCryptDestroyHash(hash);hash=nullptr;BCryptCloseAlgorithmProvider(algorithm,0);algorithm=nullptr;
            }catch(...){if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);throw;}
        }
        result.source=dop?std::unique_ptr<Source>(std::make_unique<DopSource>(segment.path.c_str(),segment.verified?segment.format.frames:0)):openSource(segment.path.c_str(),segment.verified?segment.format.frames:0);
        const auto& a=result.source->info;const auto& e=segment.format;
        if(a.rate!=e.rate||a.bits!=e.bits||a.channels!=e.channels||(!segment.verified&&a.containerBits!=e.containerBits)||a.frames!=e.frames||(!dop&&a.encoding!=e.encoding))throw Failure{E_FAIL,L"连续播放音源格式已变化"};
        result.source->seek(segment.start);return result;
    }
    static bool sameSource(const Segment& a,const Segment& b){return a.path==b.path&&a.identity==b.identity&&a.sha==b.sha&&a.format.rate==b.format.rate&&a.format.bits==b.format.bits&&a.format.channels==b.format.channels&&a.format.frames==b.format.frames&&a.format.encoding==b.format.encoding;}
    void cancelFuture(){if(cancelled)cancelled->store(true);if(future.valid()){try{future.get();}catch(...){}}futureIndex=SIZE_MAX;cancelled.reset();}
    void prefetch(){
        size_t next=index+1;while(next<segments.size()&&sameSource(segments[next],segments[index]))next++;
        if(next>=segments.size()||futureIndex==next)return;
        cancelFuture();futureIndex=next;cancelled=std::make_shared<std::atomic_bool>(false);
        auto segment=segments[next];bool mode=dop;auto cancellation=cancelled;
        future=std::async(std::launch::async,[segment,mode,cancellation]{return prepare(segment,mode,cancellation);});
    }
    void select(size_t selected,int64_t relative){
        bool same=current.source&&sameSource(segments[selected],segments[index]);
        if(!same){if(future.valid()&&futureIndex==selected){current=future.get();futureIndex=SIZE_MAX;cancelled.reset();}else{cancelFuture();current=prepare(segments[selected],dop,nullptr);}}
        index=selected;
        if(same||relative>0)current.source->seek(segments[selected].start+relative);
        prefetch();
    }
public:
    SequenceSource(const SourceSegment* items,int count,bool useDop=false):dop(useDop){
        if(!items||count<1||count>100000)throw Failure{E_INVALIDARG,L"连续播放序列无效"};
        for(int i=0;i<count;i++){
            if(!items[i].path)throw Failure{E_INVALIDARG,L"连续播放音源为空"};
            bool verified=items[i].identity&&items[i].sha256&&wcslen(items[i].sha256)==64&&items[i].total>0&&items[i].rate>0&&items[i].bits>0&&items[i].channels>0;
            SourceInfo fmt{};
            if(verified){fmt={items[i].rate,dop?24:items[i].bits,items[i].channels,dop?24:32,items[i].total,items[i].encoding};if(dop){if(fmt.rate%2||fmt.frames%2)throw Failure{E_INVALIDARG,L"DSD 时长不足完整 DoP 帧"};fmt.rate/=2;fmt.frames/=2;}}
            else{auto source=dop?std::unique_ptr<Source>(std::make_unique<DopSource>(items[i].path)):openSource(items[i].path);fmt=source->info;}
            int64_t start=items[i].rate>0?av_rescale(items[i].start,fmt.rate,items[i].rate):items[i].start;
            int64_t end=items[i].end<0?fmt.frames:items[i].rate>0?av_rescale(items[i].end,fmt.rate,items[i].rate):items[i].end;
            if(fmt.channels<1||fmt.channels>8||fmt.bits<1||fmt.bits>32||start<0||end<=start||end>fmt.frames||info.frames>INT64_MAX-(end-start))throw Failure{E_INVALIDARG,L"连续播放片段范围无效"};
            if(i==0){info=fmt;info.frames=0;}
            if(fmt.rate!=info.rate||fmt.bits!=info.bits||fmt.channels!=info.channels)throw Failure{E_INVALIDARG,L"连续播放音源格式不同"};
            info.containerBits=std::max(info.containerBits,fmt.containerBits);
            segments.push_back({items[i].path,items[i].identity?items[i].identity:L"",items[i].sha256?items[i].sha256:L"",items[i].length,start,end,info.frames,fmt,verified});info.frames+=end-start;
        }
        seek(0);
    }
    ~SequenceSource()override{cancelFuture();}
    void seek(int64_t target)override{
        if(target<0||target>=info.frames)throw Failure{E_INVALIDARG,L"连续播放定位越界"};
        size_t selected=segments.size()-1;for(size_t i=0;i<segments.size();i++)if(target<segments[i].offset+segments[i].end-segments[i].start){selected=i;break;}
        select(selected,target-segments[selected].offset);position=target;
    }
    size_t read(uint8_t* out,size_t frames)override{
        size_t written=0;int outputBytes=info.containerBits/8;
        while(written<frames&&position<info.frames){
            const auto& segment=segments[index];int inBytes=current.source->info.containerBits/8;
            size_t count=static_cast<size_t>(std::min<int64_t>(frames-written,segment.offset+segment.end-segment.start-position));
            if(!count){if(index+1<segments.size()&&sameSource(segments[index+1],segment)&&segments[index+1].start==segment.end){index++;prefetch();}else select(index+1,0);continue;}
            scratch.resize(count*info.channels*inBytes);size_t got=current.source->read(scratch.data(),count);
            if(got!=count)throw Failure{E_FAIL,L"连续播放音源提前结束"};auto* dest=out+written*info.channels*outputBytes;
            if(inBytes==outputBytes)memcpy(dest,scratch.data(),scratch.size());
            else for(size_t sample=0;sample<count*info.channels;sample++){memset(dest+sample*outputBytes,0,outputBytes-inBytes);memcpy(dest+sample*outputBytes+outputBytes-inBytes,scratch.data()+sample*inBytes,inBytes);}
            if(dop)for(size_t f=0;f<count;f++)for(int c=0;c<info.channels;c++)dest[(f*info.channels+c)*3+2]=(position+static_cast<int64_t>(f))%2?0xfa:0x05;
            written+=count;position+=count;
        }
        return written;
    }
};
