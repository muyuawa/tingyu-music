using System.Runtime.InteropServices;
using System.Text;

namespace Muyu.Core;

public sealed record AudioDevice(string Id, string Name, bool IsDefault,int State=1,string Interface="")
{
    public bool Available=>(State&1)!=0;
    public override string ToString() => Name + (IsDefault ? " · 系统默认" : "")+(Available?"":State==2?" · 已禁用":State==8?" · 未连接":" · 离线")+(Interface.Length>0?" · "+Interface:"");
}

[StructLayout(LayoutKind.Sequential)]
public struct AudioStatus
{
    public int State, Error, Rate, Bits, ContainerBits, Channels, Shared;
    public long Position, Duration, BufferedFrames, BufferCapacity, Underruns;
    public int OutputBufferFrames;
    public double Seconds => Rate > 0 ? (double)Position / Rate : 0;
}

public sealed class NativeAudio : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info { public int Rate, Bits, Channels, ContainerBits; public long Frames;public AudioEncoding Encoding; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Device
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        public int IsDefault,State;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Interface;
    }
    private const string Dll = "MuyuAudio.dll";
    [StructLayout(LayoutKind.Sequential)]private struct SupportedFormat{public int Rate,Bits,Channels;}
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]private static extern int muyu_formats(string id,[Out]SupportedFormat[] formats,int capacity);
    public static DeviceCapability Capabilities(string id){var formats=new SupportedFormat[512];int count=muyu_formats(id,formats,formats.Length);if(count<0)throw new IOException("所选设备不可用，未使用其他设备");var values=formats.Take(count).ToArray();string fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id+"|"+string.Join(';',values.Select(f=>$"{f.Rate}/{f.Bits}/{f.Channels}")))));return new(id,fingerprint,values.Select(f=>f.Rate).Distinct().ToArray(),values.Select(f=>f.Bits).Distinct().ToArray(),values.Select(f=>f.Channels).Distinct().ToArray(),values.Select(f=>new AudioFormat(f.Rate,f.Bits,f.Channels)).ToArray());}
    [StructLayout(LayoutKind.Sequential)]
    internal struct Processing { public int Rate,Bits,Channels,BufferMs;public double Gain;[MarshalAs(UnmanagedType.ByValArray,SizeConst=10)]public double[] Eq;public int Dop; }
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern int muyu_configure(IntPtr engine,Processing settings);
    internal static Processing Options(AudioSettings settings,Track track)=>new(){Rate=settings.TargetRate??0,Bits=settings.TargetBits??0,Channels=settings.TargetChannels??0,BufferMs=settings.BufferMilliseconds,Gain=settings.EffectiveGain(track),Eq=settings.Equalizer??new double[10],Dop=settings.Dsd==DsdMode.DoP?1:0};
    public void Configure(AudioSettings settings,Track track){settings.Validate();Check(muyu_configure(handle,Options(settings,track)));}
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct Segment { [MarshalAs(UnmanagedType.LPWStr)] public string Path; public long Start, End;public int Rate,Bits,Channels;public AudioEncoding Encoding;public long Total;[MarshalAs(UnmanagedType.LPWStr)]public string? Identity;[MarshalAs(UnmanagedType.LPWStr)]public string? Sha256;public long Length; }
    internal static Segment[] Segments(PlaybackRun run,IReadOnlyDictionary<string,SourceFingerprint>? evidence)=>run.Items.Select(i=>{var t=i.Track;var fingerprint=evidence?.GetValueOrDefault(t.Id);return new Segment{Path=t.Path,Start=t.StartFrame,End=t.EndFrame,Rate=t.SampleRate,Bits=t.Bits,Channels=t.Channels,Encoding=t.Encoding,Total=t.TotalFrames,Identity=fingerprint?.Identity,Sha256=fingerprint?.Sha256,Length=fingerprint?.Length??0};}).ToArray();
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] private static extern int muyu_open_sequence(IntPtr engine, [In] Segment[] segments,int count,string device,int shared);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr muyu_create();
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern void muyu_destroy(IntPtr engine);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int muyu_open(IntPtr engine, string path, string device, int shared, long start, long end);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_play(IntPtr engine);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_pause(IntPtr engine);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_mute(IntPtr engine,int muted);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_stop(IntPtr engine);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_seek(IntPtr engine, long frame);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_trim(IntPtr engine, long[] boundaries, int count);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int muyu_status(IntPtr engine, out AudioStatus status);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int muyu_error(IntPtr engine, StringBuilder text, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int muyu_probe(string path, out Info info);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private static extern int muyu_devices([Out] Device[] devices, int capacity);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)] public static extern int muyu_read_pcm(string path, long start, int frames, [Out] byte[] output, int capacity);
    private IntPtr handle = muyu_create();
    public NativeAudio() { if (handle == IntPtr.Zero) throw new InvalidOperationException("无法启动音频引擎"); }
    private void Check(int result)
    {
        if (result >= 0) return;
        var text = new StringBuilder(1024); muyu_error(handle, text, text.Capacity);
        throw new InvalidOperationException(text.Length > 0 ? text.ToString() : "音频操作失败");
    }
    public AudioStatus Status { get { Check(muyu_status(handle, out var value)); return value; } }
    public string Error { get { var text = new StringBuilder(1024); muyu_error(handle, text, text.Capacity); return text.ToString(); } }
    public void Open(Track track, string device, bool shared) => Check(muyu_open(handle, track.Path, device, shared ? 1 : 0, track.StartFrame, track.EndFrame));
    public void Open(PlaybackRun run,string device,bool shared,IReadOnlyDictionary<string,SourceFingerprint>? evidence=null) => Check(muyu_open_sequence(handle,Segments(run,evidence),run.Items.Count,device,shared?1:0));
    public void Play() => Check(muyu_play(handle));
    public void Pause() => Check(muyu_pause(handle));
    public void Mute(bool value) => Check(muyu_mute(handle,value?1:0));
    public void Stop() => Check(muyu_stop(handle));
    public void Seek(long relativeFrame) => Check(muyu_seek(handle, relativeFrame));
    public void Trim(long[] boundaries) => Check(muyu_trim(handle, boundaries, boundaries.Length));
    public void Dispose() { var old = Interlocked.Exchange(ref handle, IntPtr.Zero); if (old != IntPtr.Zero) muyu_destroy(old); }
    public static AudioInfo Probe(string path)
    {
        if (muyu_probe(path, out var info) < 0) throw new InvalidDataException($"无法解码 {Path.GetFileName(path)}：文件可能损坏、编码不支持或无法精确定位");
        return new(info.Rate, info.Bits, info.Channels, info.ContainerBits, info.Frames,info.Encoding);
    }
    public static IReadOnlyList<AudioDevice> Devices()
    {
        var buffer = new Device[128]; int count = muyu_devices(buffer, buffer.Length);
        if (count < 0) throw new InvalidOperationException("无法枚举音频输出设备");
        return buffer.Take(count).Select(d => new AudioDevice(d.Id, d.Name, d.IsDefault != 0,d.State,d.Interface)).OrderByDescending(d=>d.Available).ThenByDescending(d=>d.IsDefault).ToList();
    }
}
