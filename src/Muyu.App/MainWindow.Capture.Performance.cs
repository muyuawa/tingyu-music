using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Muyu.Core;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureRuntimeStability(string output)
    {
        var sample=new Lyrics(Enumerable.Range(0,160).Select(i=>new LyricLine(i,"长时间播放验证 · 模糊歌词与流动背景继续保留 "+i)).ToArray(),true,0,null);
        Navigate("now");await Refresh();LyricsCanvas.Load(sample);LyricsCanvas.BlurEnabled=true;LyricsCanvas.ReduceMotion=true;
        var samples=new List<object>();int peak=0;bool hiddenCleared=true,hiddenQuiet=true,throttled=true;
        using var process=Process.GetCurrentProcess();
        for(int cycle=0;cycle<4;cycle++)
        {
            AppWindow.Show();UpdateRenderActivity();
            for(int line=0;line<160;line++){LyricsCanvas.Update(line,0);await Task.Delay(12);peak=Math.Max(peak,LyricsCanvas.CachedEffectCount);}
            await Task.Delay(200);process.Refresh();samples.Add(new{cycle,private_bytes=process.PrivateMemorySize64,working_set=process.WorkingSet64,handles=process.HandleCount,cache=LyricsCanvas.CachedEffectCount});
            AppWindow.Hide();UpdateRenderActivity();await Task.Delay(100);
            long before=LyricsCanvas.DrawCount;
            for(int i=0;i<30;i++){LyricsCanvas.Update(i,0);await Task.Delay(10);}
            hiddenQuiet&=before==LyricsCanvas.DrawCount;hiddenCleared&=LyricsCanvas.CachedEffectCount==0;throttled&=clock.Interval.TotalMilliseconds==1000;
        }
        AppWindow.Show();UpdateRenderActivity();
        bool restored=clock.Interval.TotalMilliseconds==100;
        if(AppWindow.Presenter is OverlappedPresenter presenter){presenter.Minimize();UpdateRenderActivity();throttled&=clock.Interval.TotalMilliseconds==1000;presenter.Restore();UpdateRenderActivity();}
        LyricsCanvas.ReduceMotion=false;LyricsCanvas.Update(80,0);await Task.Delay(350);
        await WindowCapture.Save(this,Path.Combine(output,"retained-blurred-lyrics.png"));
        bool pass=peak>0&&peak<=48&&hiddenQuiet&&hiddenCleared&&throttled&&restored&&LyricsCanvas.BlurEnabled;
        await File.WriteAllTextAsync(Path.Combine(output,"RUNTIME_VERIFICATION.json"),JsonSerializer.Serialize(new{pass,cycles=4,line_updates=640,peak_cached_effects=peak,hidden_draws_stopped=hiddenQuiet,hidden_cache_released=hiddenCleared,hidden_and_minimized_poll_ms=1000,player_poll_ms=100,throttled,restored,blur_retained=LyricsCanvas.BlurEnabled,audio_opened=false,samples,application_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))},new JsonSerializerOptions{WriteIndented=true}));
        if(!pass)throw new InvalidOperationException("Runtime stability verification failed");
    }
}
