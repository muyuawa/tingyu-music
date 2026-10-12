using System.IO.Pipes;
using System.Text.Json;
using Muyu.Core;

internal static class AudioHostLoop
{
    public static void Run(string[] args)
    {
        if(args.Length!=1||!args[0].StartsWith("muyu-",StringComparison.Ordinal))return;
        using var engine=new NativeAudio();using var asio=new AsioEngine();
        using var pipe=new NamedPipeClientStream(".",args[0],PipeDirection.InOut);pipe.Connect(10000);
        using var reader=new StreamReader(pipe);using var writer=new StreamWriter(pipe){AutoFlush=true};
        PlaybackRun? run=null;AudioSettings settings=new();AudioActual? actual=null,unmutedActual=null;int sourceRate=0;bool useAsio=false;string[]? drivers=null;DeviceCapability? capability=null;
        AudioStatus Raw()=>useAsio?asio.Status:engine.Status;
        long ToOutput(long frame)=>sourceRate==0||Raw().Rate==0?frame:(long)Math.Round((double)frame*Raw().Rate/sourceRate);
        AudioReply Snapshot(bool failed=false,string? error=null)
        {
            var status=Raw();string message=error??(useAsio?asio.Error:engine.Error);
            if(sourceRate>0&&status.Rate>0){status.Position=(long)Math.Round((double)status.Position*sourceRate/status.Rate);status.Duration=(long)Math.Round((double)status.Duration*sourceRate/status.Rate);status.Rate=sourceRate;}
            return (run?.Reply(status,message,failed)??new(status,message,failed)) with{Actual=actual,AsioDrivers=drivers,Capability=capability};
        }
        while(reader.ReadLine() is {} line)
        {
            try
            {
                var command=JsonSerializer.Deserialize<AudioCommand>(line)??throw new ArgumentException("音频命令为空");
                switch(command.Action)
                {
                    case "open":
                        var track=command.Track??throw new ArgumentException("未选择音源");
                        var next=new PlaybackRun(command.Segments is {Length:>0}?command.Segments:[new(command.ItemId??Guid.NewGuid(),track)]);
                        var first=next.Items[0].Track;if(first.Id!=track.Id||first.Path!=track.Path||first.StartFrame!=track.StartFrame||first.EndFrame!=track.EndFrame||first.SampleRate!=track.SampleRate||first.Bits!=track.Bits||first.Channels!=track.Channels)throw new ArgumentException("连续播放序列与请求曲目不一致");
                        var requested=command.Settings??new AudioSettings(Device:command.Device??"",Backend:command.Shared?AudioBackend.WasapiShared:AudioBackend.WasapiExclusive);
                        requested.Validate();
                        engine.Stop();asio.Dispose();run=null;actual=null;settings=requested;useAsio=settings.Backend==AudioBackend.Asio;sourceRate=track.SampleRate;
                        if(useAsio)asio.Open(next,settings,command.Evidence);else{engine.Configure(settings,track);engine.Open(next,settings.Device,settings.Backend==AudioBackend.WasapiShared,command.Evidence);}
                        run=next;var opened=Raw();
                        actual=new(settings.Revision,settings.Device,settings.Backend,track.SampleRate,track.Bits,track.Channels,opened.Rate,opened.Bits,opened.ContainerBits,opened.Channels,settings.BufferMilliseconds,opened.OutputBufferFrames,settings.Processing||track.Encoding==AudioEncoding.FloatPcm||AudioAuthorization.Kind(track)=="DSD"&&settings.Dsd==DsdMode.Pcm||settings.Backend==AudioBackend.WasapiShared,settings.Dsd==DsdMode.DoP?"DoP 1.1 · DSD 原始位封装；接收设备必须支持 DoP":settings.Backend==AudioBackend.WasapiShared?"Windows 共享混音与格式转换":settings.Processing?"已授权应用处理 PCM":track.Encoding==AudioEncoding.FloatPcm?"已明确将浮点 PCM 转成 32 位整数；超出满幅的数值会截断":AudioAuthorization.Kind(track)=="DSD"?"已明确将 DSD 解码为 PCM":"解码后未启用应用处理；整数 WAV/FLAC 保留有效样本；硬件内部处理未经验证");
                        unmutedActual=actual;
                        break;
                    case "play":if(useAsio)asio.Play();else engine.Play();break;
                    case "pause":if(useAsio)asio.Pause();else engine.Pause();break;
                    case "mute":
                        if(command.Frame is not(0 or 1)||run==null||unmutedActual==null)throw new InvalidOperationException("当前没有可静音的输出");
                        bool muted=command.Frame==1;
                        if(settings.Dsd==DsdMode.DoP&&muted)throw new InvalidOperationException("DoP 不允许软件静音，请使用设备硬件静音");
                        if(useAsio)asio.Mute(muted);else engine.Mute(muted);
                        actual=muted?unmutedActual with{Muted=true,PcmModified=true,Description="软件静音 · 提交全零 PCM；播放时钟继续"}:unmutedActual;
                        break;
                    case "stop":engine.Stop();asio.Dispose();run=null;actual=null;break;
                    case "seek":long frame=run==null?command.Frame:run.SeekFrame(command.ItemId??run.At(Snapshot().Status.Position).ItemId,command.Frame);if(useAsio)asio.Seek(ToOutput(frame));else engine.Seek(ToOutput(frame));break;
                    case "trim":
                        if(run is {Continuous:true}&&Raw().State is 1 or 2){var boundaries=run.Boundaries.Select(ToOutput).ToArray();if(useAsio)asio.Trim(boundaries);else engine.Trim(boundaries);long duration=Raw().Duration;int boundary=Array.IndexOf(boundaries,duration);if(boundary<0)throw new InvalidOperationException("输出边界不一致");run=new(run.Items.Take(boundary+1));}break;
                    case "asio-drivers":drivers=AsioEngine.Drivers();break;
                    case "capability":capability=command.Settings?.Backend==AudioBackend.Asio?asio.Capabilities(command.Device!):NativeAudio.Capabilities(command.Device!);break;
                    case "status":break;
                    case "quit":return;
                    default:throw new ArgumentException("未知音频命令");
                }
                writer.WriteLine(JsonSerializer.Serialize(Snapshot(),AudioProtocol.Options));
            }
            catch(Exception ex){writer.WriteLine(JsonSerializer.Serialize(Snapshot(true,ex.Message),AudioProtocol.Options));}
        }
    }
}
