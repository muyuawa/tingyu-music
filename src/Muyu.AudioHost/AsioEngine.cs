using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Muyu.Core;
using NAudio.Wave.Asio;

sealed class AsioEngine:IDisposable
{
    private AsioDriverExt? driver;
    private NativeSource? source;
    private byte[] interleaved=[],channel=[];
    private int frames,bytes,outputBytes,channels,rate,bits,state;
    private long submitted,baseFrame,duration,limit,drainFrames;
    private string error="";
    private AsioSampleType type;
    private string driverName="";
    private bool driverStopped=true,dop;
    private int muted;
    public string Error=>error;
    public static string[] Drivers()=>AsioDriver.GetAsioDriverNames();
    public DeviceCapability Capabilities(string name)
    {
        var selected=driver!=null&&driverName==name?driver:new AsioDriverExt(AsioDriver.GetAsioDriverByName(name));
        try{int[] rates=new[]{44100,48000,88200,96000,176400,192000,352800,384000,705600,768000}.Where(rate=>selected.IsSampleRateSupported(rate)).ToArray();var caps=selected.Capabilities;string signature=name+"|"+string.Join(',',rates)+"|"+string.Join(',',caps.OutputChannelInfos.Select(c=>c.name+":"+c.type))+"|"+caps.BufferPreferredSize+"|"+selected.Driver.GetDriverVersion();string fingerprint=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(signature)));return new(name,fingerprint,rates,[16,24,32],Enumerable.Range(1,caps.NbOutputChannels).ToArray());}finally{if(selected!=driver)selected.ReleaseDriver();}
    }
    public void Open(PlaybackRun run,AudioSettings settings,IReadOnlyDictionary<string,SourceFingerprint>? evidence=null)
    {
        Dispose();error="";dop=settings.Dsd==DsdMode.DoP;Volatile.Write(ref muted,0);
        try
        {
            source=new(run,settings,evidence);var info=source.Format;rate=info.SampleRate;bits=info.Bits;bytes=info.ContainerBits/8;channels=info.Channels;duration=limit=info.Frames;
            driver=new(AsioDriver.GetAsioDriverByName(settings.Device));
            driverName=settings.Device;
            if(!driver.IsSampleRateSupported(rate)||driver.Capabilities.NbOutputChannels<channels)throw new InvalidOperationException("ASIO 驱动不支持所选采样率或声道数");
            type=driver.Capabilities.OutputChannelInfos[0].type;
            if(settings.Dsd==DsdMode.DoP&&type==AsioSampleType.Float32LSB)throw new InvalidOperationException("DoP 必须使用整数 ASIO 通道");
            if(driver.Capabilities.OutputChannelInfos.Take(channels).Any(c=>c.type!=type))throw new InvalidOperationException("ASIO 各声道样本格式不一致");
            outputBytes=type switch{AsioSampleType.Int16LSB=>2,AsioSampleType.Int24LSB=>3,AsioSampleType.Int32LSB=>4,AsioSampleType.Float32LSB when bits<=24=>4,_=>throw new InvalidOperationException("此 ASIO 格式无法保证当前有效样本精度，请明确调整输出位深")};
            if(type!=AsioSampleType.Float32LSB&&outputBytes*8<bits)throw new InvalidOperationException("ASIO 容器位深不足，请明确授权位深转换");
            driver.SetSampleRate(rate);driver.FillBufferCallback=Fill;
            driver.ResetRequestCallback=()=>{error="ASIO 驱动设置已变化，请重新选择输出并继续";Volatile.Write(ref state,4);};
            frames=driver.CreateBuffers(channels,0,false);driver.SetChannelOffset(0,0);
            interleaved=new byte[frames*channels*bytes];channel=new byte[frames*outputBytes];submitted=baseFrame=drainFrames=0;state=1;
        }
        catch{Dispose();state=4;throw;}
    }
    private void Fill(IntPtr[] inputs,IntPtr[] outputs)
    {
        try
        {
            int count=Volatile.Read(ref state)==2?(int)Math.Min(frames,Math.Max(0,limit-baseFrame-Interlocked.Read(ref submitted))):0;
            int got=count>0?source!.Read(interleaved,count):0;
            if(got!=count)throw new IOException("ASIO 音源提前结束");
            bool silence=Volatile.Read(ref muted)!=0;
            for(int c=0;c<channels;c++)
            {
                Array.Clear(channel);
                for(int f=0;!silence&&f<got;f++){
                    int sample=0,offset=(f*channels+c)*bytes;
                    for(int b=0;b<bytes;b++)sample|=interleaved[offset+b]<<(8*b);
                    sample<<=32-bytes*8;
                    if(type==AsioSampleType.Float32LSB)BinaryPrimitives.WriteSingleLittleEndian(channel.AsSpan(f*4),sample/2147483648f);
                    else for(int b=0;b<outputBytes;b++)channel[f*outputBytes+b]=(byte)(sample>>(8*(4-outputBytes+b)));
                }
                Marshal.Copy(channel,0,outputs[c],channel.Length);
            }
            Interlocked.Add(ref submitted,got);
            if(got==0&&Volatile.Read(ref state)==2){drainFrames+=frames;if(drainFrames>=driver!.Capabilities.OutputLatency+2L*frames)Volatile.Write(ref state,3);}
        }
        catch(Exception ex){error=ex.Message;Volatile.Write(ref state,4);Array.Clear(channel);foreach(var output in outputs)Marshal.Copy(channel,0,output,channel.Length);}
    }
    public AudioStatus Status {get{if(state is 3 or 4&&!driverStopped){driver?.Stop();driverStopped=true;}long played=Math.Max(0,Interlocked.Read(ref submitted)-(driver?.Capabilities.OutputLatency??0)-frames);return new(){State=Volatile.Read(ref state),Rate=rate,Bits=bits,ContainerBits=outputBytes*8,Channels=channels,Position=state==3?limit:Math.Min(limit,baseFrame+played),Duration=limit,OutputBufferFrames=frames};}}
    public void Play(){if(driver==null)throw new InvalidOperationException("ASIO 尚未打开");if(state==2)return;Volatile.Write(ref state,2);driver.Start();driverStopped=false;}
    public void Pause(){if(driver==null)return;driver.Stop();driverStopped=true;long position=Status.Position;source!.Seek(Math.Min(position,duration-1));baseFrame=position;submitted=drainFrames=0;state=1;}
    public void Seek(long frame){bool resume=state==2;driver?.Stop();driverStopped=true;frame=Math.Clamp(frame,0,limit-1);source!.Seek(frame);baseFrame=frame;submitted=drainFrames=0;state=1;if(resume)Play();}
    public void Trim(long[] boundaries){bool resume=state==2;Pause();limit=boundaries.FirstOrDefault(x=>x>Status.Position,limit);if(resume&&Status.Position<limit)Play();}
    public void Mute(bool value){if(value&&dop)throw new InvalidOperationException("DoP 不允许软件静音，请使用设备硬件静音");Volatile.Write(ref muted,value?1:0);}
    public void Dispose(){if(driver!=null){driver.Stop();driverStopped=true;driver.ResetRequestCallback=null;driver.ReleaseDriver();driver=null;}source?.Dispose();source=null;state=0;}
}
