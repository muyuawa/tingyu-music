using System.Runtime.InteropServices;
namespace Muyu.Core;
public sealed class NativeSource:IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Info {public int Rate,Bits,Channels,Container;public long Frames;public AudioEncoding Encoding;}
    [DllImport("MuyuAudio.dll",CallingConvention=CallingConvention.Cdecl)]private static extern IntPtr muyu_source_create([In]NativeAudio.Segment[] entries,int count,NativeAudio.Processing options,out Info info);
    [DllImport("MuyuAudio.dll",CallingConvention=CallingConvention.Cdecl)]private static extern void muyu_source_destroy(IntPtr handle);
    [DllImport("MuyuAudio.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int muyu_source_read(IntPtr handle,[Out]byte[] data,int frames,int capacity);
    [DllImport("MuyuAudio.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int muyu_source_seek(IntPtr handle,long frame);
    [DllImport("MuyuAudio.dll",CallingConvention=CallingConvention.Cdecl)]private static extern int muyu_source_wait(IntPtr handle,int frames);
    private IntPtr handle;
    public AudioInfo Format {get;}
    public NativeSource(PlaybackRun run,AudioSettings settings,IReadOnlyDictionary<string,SourceFingerprint>? evidence=null){settings.Validate();handle=muyu_source_create(NativeAudio.Segments(run,evidence),run.Items.Count,NativeAudio.Options(settings,run.Items[0].Track),out var info);if(handle==IntPtr.Zero)throw new IOException("无法建立精确音频预读，音源可能已变化或不可读");Format=new(info.Rate,info.Bits,info.Channels,info.Container,info.Frames,info.Encoding);}
    public int Read(byte[] output,int frames,bool wait=false){if(wait&&muyu_source_wait(handle,frames)<0)throw new IOException("音频预读超时");int result=muyu_source_read(handle,output,frames,output.Length);if(result<0)throw new IOException("音频预读不足或解码失败");return result;}
    public void Seek(long frame){if(muyu_source_seek(handle,frame)<0)throw new IOException("音频定位失败");}
    public void Dispose(){var old=Interlocked.Exchange(ref handle,IntPtr.Zero);if(old!=IntPtr.Zero)muyu_source_destroy(old);}
}
