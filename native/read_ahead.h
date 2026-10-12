#pragma once
#include <optional>

// The producer alone touches the decoder. The consumer holds the mutex only
// while copying PCM; decoder I/O never owns this mutex.
class ReadAheadSource final : public Source {
    std::unique_ptr<Source> decoder;
    mutable std::mutex mutex;
    std::condition_variable changed;
    std::thread producer;
    std::vector<uint8_t> ring;
    size_t capacity, bytesPerFrame, head=0, ready=0;
    uint64_t generation=0;
    int64_t requestedFrame=0;
    bool quitting=false, needsSeek=true, eof=false;
    std::optional<Failure> failure;

    void produce() {
        const size_t chunkFrames=std::min<size_t>(4096,capacity);
        std::vector<uint8_t> scratch(chunkFrames*bytesPerFrame);
        while(true) {
            uint64_t epoch;
            int64_t target;
            size_t count;
            bool reposition;
            {
                std::unique_lock lock(mutex);
                changed.wait(lock,[&]{return quitting||needsSeek||(!eof&&!failure&&ready<capacity);});
                if(quitting) return;
                epoch=generation; target=requestedFrame; reposition=needsSeek;
                needsSeek=false; count=std::min(chunkFrames,capacity-ready);
            }
            size_t got=0;
            std::optional<Failure> error;
            try {
                if(reposition) decoder->seek(target);
                got=decoder->read(scratch.data(),count);
                if(got>count) throw Failure{E_FAIL,L"解码器返回了超出请求的样本数"};
            } catch(const Failure& ex) { error=ex; }
              catch(...) { error=Failure{E_FAIL,L"预读解码失败，播放已停止"}; }
            {
                std::lock_guard lock(mutex);
                if(epoch!=generation) continue;
                if(error) failure=std::move(error);
                else {
                    size_t tail=(head+ready)%capacity;
                    size_t first=std::min(got,capacity-tail);
                    memcpy(ring.data()+tail*bytesPerFrame,scratch.data(),first*bytesPerFrame);
                    memcpy(ring.data(),scratch.data()+first*bytesPerFrame,(got-first)*bytesPerFrame);
                    ready+=got; eof=got<count;
                }
            }
            changed.notify_all();
        }
    }
public:
    explicit ReadAheadSource(std::unique_ptr<Source> input,size_t capacityFrames=0) : decoder(std::move(input)) {
        info=decoder->info;
        bytesPerFrame=static_cast<size_t>(info.channels)*info.containerBits/8;
        capacity=capacityFrames?capacityFrames:static_cast<size_t>(info.rate)*2;
        if(!capacity||!bytesPerFrame) throw Failure{E_INVALIDARG,L"预读缓冲参数无效"};
        ring.resize(capacity*bytesPerFrame);
        producer=std::thread([this]{produce();});
    }
    ~ReadAheadSource() override {
        {std::lock_guard lock(mutex);quitting=true;}
        changed.notify_all(); producer.join();
    }
    bool waitReady(size_t frames,std::chrono::milliseconds timeout=std::chrono::seconds(10)) {
        if(frames>capacity) throw Failure{E_INVALIDARG,L"预读请求超出缓冲容量"};
        std::unique_lock lock(mutex);
        bool completed=changed.wait_for(lock,timeout,[&]{return quitting||failure||(!needsSeek&&(ready>=frames||eof));});
        if(failure) throw *failure;
        return completed&&!quitting;
    }
    void seek(int64_t frame) override {
        if(frame<0||frame>=info.frames) throw Failure{E_INVALIDARG,L"预读定位超出音频范围"};
        {
            std::lock_guard lock(mutex);
            ++generation; requestedFrame=frame; needsSeek=true;
            head=ready=0; eof=false; failure.reset();
        }
        changed.notify_all();
        size_t prime=static_cast<size_t>(std::min<int64_t>(std::min<size_t>(capacity,info.rate/2),info.frames-frame));
        if(!waitReady(prime)) throw Failure{HRESULT_FROM_WIN32(ERROR_TIMEOUT),L"音频预读超时，播放已停止"};
    }
    size_t read(uint8_t* out,size_t frames) override {
        size_t count;
        {
            std::lock_guard lock(mutex);
            if(failure) throw *failure;
            if(ready<frames&&!eof) throw Failure{AUDCLNT_E_BUFFER_ERROR,L"音频预读不足，播放已停止；请检查源文件所在磁盘"};
            count=std::min(frames,ready);
            size_t first=std::min(count,capacity-head);
            memcpy(out,ring.data()+head*bytesPerFrame,first*bytesPerFrame);
            memcpy(out+first*bytesPerFrame,ring.data(),(count-first)*bytesPerFrame);
            head=(head+count)%capacity; ready-=count;
        }
        changed.notify_all(); return count;
    }
    size_t buffered() const {std::lock_guard lock(mutex);return ready;}
    size_t capacityFrames() const {return capacity;}
};
