using Microsoft.UI.Xaml;
using Muyu.Core;
using System.Text.Json;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task<bool> CaptureGlassGloss(string output)
    {
        bool frozen=captureFlowFrozen;var theme=Root.RequestedTheme;var palette=albumColors;
        var cases=new List<object>();bool pass=true;
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        try
        {
            byte[] colors=Enumerable.Range(0,1024).SelectMany(i=>{var c=new RgbColor[]{new(24,55,100),new(242,216,112),new(218,68,166),new(31,214,218)}[i%4];return new byte[]{c.R,c.G,c.B,255};}).ToArray();
            albumColors=AlbumColorEngine.Analyze(colors);
            foreach(var global in new[]{ElementTheme.Dark,ElementTheme.Light})
            foreach(string target in new[]{"now","home"})
            {
                Root.RequestedTheme=global;Navigate(target);await Refresh();ApplyVisualPreferences();await Task.Delay(900);
                if(target=="now"){NoLyrics.Visibility=Visibility.Collapsed;LyricsCanvas.Load(new Lyrics(Enumerable.Range(0,40).Select(i=>new LyricLine(i*3,$"{i:00} · 多种封面颜色柔和流动，歌词穿过玻璃")).ToArray(),true,0,null));LyricsCanvas.Update(60,0);LyricsCanvas.ResumeFollowing(true);}
                var surface=target=="now"?PlayButton:(FrameworkElement)NavigationGlass;
                glassRenderer!.DiagnosticLeave(surface);glassRenderer.DiagnosticLeave(TransportSurface);
                var before=glassRenderer!.PointerState(surface);
                await Task.Delay(160);
                await WindowCapture.Save(this,Path.Combine(output,$"gloss-{global.ToString().ToLowerInvariant()}-{target}-idle.png"));
                if(global==ElementTheme.Light&&target=="home")
                {
                    NavigationGlass.Opacity=0;await Task.Delay(160);
                    await WindowCapture.Save(this,Path.Combine(output,"gloss-light-home-without-material.png"));
                    NavigationGlass.Opacity=1;await Task.Delay(160);
                }
                var angles=new List<float>();
                for(int frame=0;frame<6;frame++)
                {
                    float phase=frame*MathF.PI/3;
                    glassRenderer.DiagnosticPointer(surface,.5f+.43f*MathF.Cos(phase),.5f+.43f*MathF.Sin(phase));
                    glassRenderer.DiagnosticPointer(TransportSurface,.5f+.43f*MathF.Cos(phase),.5f+.43f*MathF.Sin(phase));
                    await Task.Delay(160);angles.Add(glassRenderer.PointerState(surface).Angle);
                    await WindowCapture.Save(this,Path.Combine(output,$"gloss-{global.ToString().ToLowerInvariant()}-{target}-{frame:00}.png"));
                }
                var after=glassRenderer.PointerState(surface);
                glassRenderer.DiagnosticLeave(surface);glassRenderer.DiagnosticLeave(TransportSurface);
                var resting=glassRenderer.PointerState(surface);
                bool changed=after.Shader&&after.Updates-before.Updates==6&&angles.Distinct().Count()==6&&(target=="now"||after.InputSeparate)&&before.Glare==0&&after.Glare>0&&resting.Glare==0;
                pass&=changed;
                cases.Add(new{theme=global.ToString(),page=target,pass=changed,angles,live_gpu_shader=after.Shader,pointer_input_separate_from_non_hit_test_glass=after.InputSeparate,idle_glare=before.Glare,active_glare=after.Glare,exit_glare=resting.Glare,soft_rim=resting.Rim,surface_bounds=CaptureBounds(surface),transport_bounds=CaptureBounds(TransportSurface)});
            }
            await File.WriteAllTextAsync(Path.Combine(output,"GLASS_GLOSS_VERIFICATION.json"),JsonSerializer.Serialize(new{pass,cases,actual_integrated_window=true,background_motion_frozen=true,frames_per_case=6,interval_ms=160,input="normalized positions passed through the same material response as the routed pointer handler; not OS mouse injection",audio_output_opened=false},new JsonSerializerOptions{WriteIndented=true}));
            return pass;
        }
        finally{albumColors=palette;Root.RequestedTheme=theme;captureFlowFrozen=frozen;flowingBackdrop?.SetForeground(!frozen);ApplyVisualPreferences();}
    }
}
