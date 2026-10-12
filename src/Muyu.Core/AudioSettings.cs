using System.Security.Cryptography;
using System.Text;
namespace Muyu.Core;
public enum AudioBackend { WasapiExclusive,WasapiShared,Asio }
public enum DsdMode { Pcm,DoP }
public enum ReplayGainMode { Off,Track,Album }
public sealed record AudioSettings(long Revision=1,AudioBackend Backend=AudioBackend.WasapiExclusive,string Device="",int BufferMilliseconds=100,int? TargetRate=null,int? TargetBits=null,int? TargetChannels=null,double GainDb=0,ReplayGainMode ReplayGain=ReplayGainMode.Off,bool PreventClipping=true,double[]? Equalizer=null,DsdMode Dsd=DsdMode.Pcm,bool FollowSystemDefault=false)
{
    public bool Processing=>TargetRate!=null||TargetBits!=null||TargetChannels!=null||GainDb!=0||ReplayGain!=ReplayGainMode.Off||Equalizer?.Any(x=>x!=0)==true;
    public void Validate()
    {
        if(Revision<1||!Enum.IsDefined(Backend)||!Enum.IsDefined(ReplayGain)||!Enum.IsDefined(Dsd)||BufferMilliseconds is <10 or >1000||TargetRate is not(null or 44100 or 48000 or 88200 or 96000 or 176400 or 192000 or 352800 or 384000)||TargetBits is not(null or 16 or 24 or 32)||TargetChannels is not(null or 1 or 2)||!double.IsFinite(GainDb)||GainDb is <-60 or >12||Equalizer!=null&&(Equalizer.Length!=10||Equalizer.Any(x=>!double.IsFinite(x)||x is <-12 or >12)))throw new FormatException("音频设置超出有效范围");
        if(Dsd==DsdMode.DoP&&(Backend==AudioBackend.WasapiShared||Processing))throw new FormatException("DoP 需要独占输出，并关闭采样转换、音量、ReplayGain 和均衡器");
        if(FollowSystemDefault&&Backend==AudioBackend.Asio)throw new FormatException("ASIO 需要固定选择驱动");
    }
    public string StrategyHash=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{Backend}|{TargetRate}|{TargetBits}|{TargetChannels}|{GainDb:R}|{ReplayGain}|{PreventClipping}|{string.Join(',',Equalizer??[])}|{Dsd}")));
    // Volume and bookkeeping revisions do not introduce a new processing policy.
    public string ProcessingPolicyHash=>(this with{GainDb=0}).StrategyHash;
    public double EffectiveGain(Track track){double gain=GainDb;if(ReplayGain!=ReplayGainMode.Off){gain+=(ReplayGain==ReplayGainMode.Album?track.Metadata?.AlbumGainDb:track.Metadata?.TrackGainDb)??0;if(PreventClipping){double? peak=ReplayGain==ReplayGainMode.Album?track.Metadata?.AlbumPeak:track.Metadata?.TrackPeak;if(peak>0)gain=Math.Min(gain,-20*Math.Log10(peak.Value));}}return gain;}
}
public sealed record AudioAuthorization(Guid Id,string Device,string CapabilityHash,int SourceRate,int SourceBits,int SourceChannels,string StrategyHash,DateTimeOffset Created,string SourceKind="PCM")
{
    public static string Kind(Track track)=>track.Encoding==AudioEncoding.FloatPcm?"浮点 PCM":track.Encoding==AudioEncoding.Dsd?"DSD":track.Encoding==AudioEncoding.Lossy?"解码音频":Path.GetExtension(track.Path).ToLowerInvariant() switch{".dsf" or ".dff"=>"DSD",".mp3" or ".aac" or ".ogg" or ".opus" or ".m4a"=>"解码音频",_=>"PCM"};
    public bool Matches(AudioSettings settings,Track source,string fingerprint)=>Device==settings.Device&&CapabilityHash==fingerprint&&SourceRate==source.SampleRate&&SourceBits==source.Bits&&SourceChannels==source.Channels&&StrategyHash==settings.StrategyHash&&SourceKind==Kind(source);
}
public sealed record AudioActual(long SettingsRevision,string Device,AudioBackend Backend,int SourceRate,int SourceBits,int SourceChannels,int OutputRate,int OutputBits,int ContainerBits,int OutputChannels,int RequestedBufferMs,int ActualBufferFrames,bool PcmModified,string Description,bool Muted=false);
public sealed record AudioPolicyGrant(Guid Id,string Device,string CapabilityHash,string PolicyHash,string SourceKind,int SourceChannels,DateTimeOffset Created)
{
    public static AudioPolicyGrant Accept(AudioSettings settings,Track track,string fingerprint)=>new(Guid.NewGuid(),settings.Device,fingerprint,settings.ProcessingPolicyHash,AudioAuthorization.Kind(track),track.Channels,DateTimeOffset.UtcNow);
    public bool Matches(AudioSettings settings,Track track,string fingerprint)=>Device==settings.Device&&CapabilityHash==fingerprint&&PolicyHash==settings.ProcessingPolicyHash&&SourceKind==AudioAuthorization.Kind(track)&&SourceChannels==track.Channels;
}
public sealed record AudioFormat(int Rate,int Bits,int Channels){public override string ToString()=>$"{Bits}-bit / {Rate/1000.0:0.###} kHz / {Channels} 声道";}
public sealed record DeviceCapability(string Device,string Fingerprint,int[] Rates,int[] Bits,int[] Channels,AudioFormat[]? Formats=null);
