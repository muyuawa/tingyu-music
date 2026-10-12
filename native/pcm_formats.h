#pragma once
// Descriptor variants change neither sample rate nor channel count. Wider integer
// containers are filled with left-aligned zero padding by the existing renderer.
struct PcmFormat {
    WAVEFORMATEXTENSIBLE value{};
    WAVEFORMATEX* data(){return &value.Format;}
    const WAVEFORMATEX* data()const{return &value.Format;}
    int validBits()const{return value.Format.wFormatTag==WAVE_FORMAT_PCM?value.Format.wBitsPerSample:value.Samples.wValidBitsPerSample;}
};
static std::vector<PcmFormat> pcmFormats(int rate,int valid,int container,int channels,DWORD mask){
    std::vector<PcmFormat> formats;
    auto append=[&](int bits,int precision,bool simple){
        PcmFormat candidate;auto& fmt=candidate.value;
        fmt.Format={static_cast<WORD>(simple?WAVE_FORMAT_PCM:WAVE_FORMAT_EXTENSIBLE),static_cast<WORD>(channels),static_cast<DWORD>(rate),static_cast<DWORD>(rate*channels*bits/8),static_cast<WORD>(channels*bits/8),static_cast<WORD>(bits),static_cast<WORD>(simple?0:22)};
        fmt.Samples.wValidBitsPerSample=static_cast<WORD>(precision);fmt.dwChannelMask=mask;fmt.SubFormat=KSDATAFORMAT_SUBTYPE_PCM;
        if(std::none_of(formats.begin(),formats.end(),[&](const auto& existing){return memcmp(&existing.value,&fmt,sizeof(fmt))==0;}))formats.push_back(candidate);
    };
    // Processing sources store left-aligned samples in 32 bits internally. That
    // storage width need not be the hardware format: Realtek can report 32/16
    // supported yet fail Initialize. Prefer a packed container of equal precision.
    int packed=valid<=16?16:valid<=24?24:32;
    for(int bits:{packed,container,16,24,32})if(bits>=valid){
        append(bits,valid,false);
        if(channels<=2&&bits==valid)append(bits,bits,true);
        if(bits!=valid){append(bits,bits,false);if(channels<=2)append(bits,bits,true);}
    }
    return formats;
}
static bool formatRejected(HRESULT code){return code==AUDCLNT_E_UNSUPPORTED_FORMAT||code==E_INVALIDARG;}
template<class Supports,class Initialize>
static HRESULT negotiatePcm(const std::vector<PcmFormat>& choices,PcmFormat& selected,Supports supports,Initialize initialize){
    HRESULT last=AUDCLNT_E_UNSUPPORTED_FORMAT;
    for(const auto& choice:choices){
        HRESULT supported=supports(choice);
        if(supported!=S_OK){if(FAILED(supported)&&!formatRejected(supported))return supported;continue;}
        last=initialize(choice);
        if(SUCCEEDED(last)){selected=choice;return last;}
        // A busy/disconnected device is not a format mismatch; never mask it.
        // Some drivers report support for padded descriptors but fail endpoint
        // creation. Try only equivalent descriptors on this same endpoint.
        if(!formatRejected(last)&&last!=AUDCLNT_E_ENDPOINT_CREATE_FAILED)return last;
    }
    return last;
}
