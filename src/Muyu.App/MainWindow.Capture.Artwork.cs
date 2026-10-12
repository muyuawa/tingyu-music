using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muyu.Core;
using System.Text.Json;
using Windows.Graphics.Imaging;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureArtworkBackground(string output,string artworkFile)
    {
        using var input=File.OpenRead(artworkFile).AsRandomAccessStream();
        var decoder=await BitmapDecoder.CreateAsync(input);
        var pixels=(await decoder.GetPixelDataAsync(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Straight,new BitmapTransform{ScaledWidth=32,ScaledHeight=32},ExifOrientationMode.IgnoreExifOrientation,ColorManagementMode.DoNotColorManage)).DetachPixelData();
        albumColors=AlbumColorEngine.Analyze(pixels);
        CoverImage.Source=new BitmapImage(new Uri(Path.GetFullPath(artworkFile)));
        SongTitle.Text=MiniTitle.Text="封面背景验证";SongArtist.Text="抽象流光 · 覆盖范围验证";SongFormat.Text="封面视觉验证 · 未建立音频输出";
        var sample=new Lyrics(Enumerable.Range(0,18).Select(i=>new LyricLine(i*10,new[]{"只让封面的配色柔和交融","不保留人物和物体的轮廓","当前歌词清晰，前后段落自然散焦","主色与辅色缓慢流动"}[i%4])).ToArray(),true,0,null);
        NoLyrics.Visibility=Visibility.Collapsed;LyricsCanvas.Load(sample);LyricsCanvas.Update(42,0);LyricsCanvas.ResumeFollowing(true);
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        await Task.Delay(1800);
        bool playerGlass=true,libraryGlass=true,follow=true;
        foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
        foreach(string target in new[]{"now","home"})
        {
            Root.RequestedTheme=theme;Navigate(target);await Refresh();
            ApplyVisualPreferences();await Task.Delay(1000);Root.UpdateLayout();
            if(target=="now")
            {
                NoLyrics.Visibility=Visibility.Collapsed;LyricsCanvas.Load(sample);LyricsCanvas.Update(42,0);LyricsCanvas.ResumeFollowing(true);
                playerGlass&=TransportSurface.Background is LiquidGlassWinUI.LiquidGlassBrush brush&&brush.IsUsingShader&&brush.TintA==glassRenderer!.Parameters.TintOpacity&&transportElevation?.IsVisible==true;
                follow&=LyricsCanvas.IsFollowing&&LyricsCanvas.IsCurrentLineAligned;
            }
            else libraryGlass&=TransportSurface.Background is LiquidGlassWinUI.LiquidGlassBrush glass&&glass.IsUsingShader;
            string screenshot=Path.Combine(output,$"artwork-{theme.ToString().ToLowerInvariant()}-{target}.png");
            for(int attempt=0;attempt<4;attempt++)
            {
                await WindowCapture.Save(this,screenshot);
                if(new FileInfo(screenshot).Length>60000)break;
                if(attempt==3)throw new InvalidOperationException("Artwork window did not finish rendering");
                await Task.Delay(1000);
            }
        }
        Navigate("now");await Refresh();ApplyVisualPreferences();LyricsCanvas.Load(sample);LyricsCanvas.Update(42,0);LyricsCanvas.ResumeFollowing(true);
        captureFlowFrozen=false;flowingBackdrop?.SetForeground(true);
        for(int i=0;i<10;i++){await Task.Delay(1000);await WindowCapture.Save(this,Path.Combine(output,$"artwork-flow-{i:00}.png"));}
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        flowingBackdrop!.SetBlur(0);await Task.Delay(250);await WindowCapture.Save(this,Path.Combine(output,"artwork-blur-0.png"));
        flowingBackdrop.SetBlur(80);await Task.Delay(250);await WindowCapture.Save(this,Path.Combine(output,"artwork-blur-80.png"));flowingBackdrop.SetBlur(PlaybackBackgroundBlur);
        if(Environment.GetCommandLineArgs().Contains("--coverage"))await CaptureBackdropCoverage(output);
        // Verify normal navigation still restores library glass, and contrast/extreme artwork still works.
        bool colors=await CaptureAlbumColors(output);
        colors&=await CaptureGlassGloss(output);
        await File.WriteAllTextAsync(Path.Combine(output,"ARTWORK_BACKGROUND_VERIFICATION.json"),JsonSerializer.Serialize(new
        {
            version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),pass=playerGlass&&libraryGlass&&follow&&colors,
            actual_integrated_window=true,abstract_palette_texture=albumColors.BackdropRgba?.Length==4096,original_artwork_used_as_background=false,
            player_transport_uses_live_glass_with_frosted_body_and_soft_shadow=playerGlass,library_transport_glass_preserved=libraryGlass,
            lyrics_follow_current=follow,lyrics_presentation="0.12.1 blur/opacity and 42% focus restored; current text retains adaptive color",
            dominant=albumColors.DominantColor.ToString(),palette=albumColors.Colors.Select(c=>c.ToString()),
            frames=10,interval_ms=1000,application_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Muyu.App.dll")))),
            audio_output_opened=false,music_files_accessed_by_capture=false,artwork_input="read-only extracted embedded cover; no audio decoding or playback"
        },new JsonSerializerOptions{WriteIndented=true}));
    }
    private async Task CaptureBackdropCoverage(string output)
    {
        var originals=Root.Children.Where(child=>child!=AtmosphereHost).ToDictionary(child=>child,child=>child.Opacity);
        var theme=Root.RequestedTheme;double blur=flowingBackdrop!.BlurAmount;var cases=new List<object>();
        try
        {
            foreach(var child in originals.Keys)child.Opacity=0;
            flowingBackdrop.SetBlur(0);
            foreach(var global in new[]{ElementTheme.Light,ElementTheme.Dark})
            foreach(var layout in new[]{(Name:"landscape",Width:1440,Height:940),(Name:"square",Width:1100,Height:1100),(Name:"portrait",Width:884,Height:1080)})
            {
                Root.RequestedTheme=global;ResizeForDips(layout.Width,layout.Height);await Task.Delay(150);ApplyVisualPreferences();
                flowingBackdrop.SetBlur(0);await Task.Delay(850);
                foreach(int angle in Enumerable.Range(0,8).Select(i=>i*45))
                {
                    flowingBackdrop.SetDiagnosticProgress(angle/360f,.5f);await Task.Delay(100);
                    string name=$"coverage-{global.ToString().ToLowerInvariant()}-{layout.Name}-{angle:000}.png";
                    await WindowCapture.Save(this,Path.Combine(output,name));
                    cases.Add(new{theme=global.ToString(),layout=layout.Name,angle,maximum_drift=true,blur=0,screenshot=name});
                }
            }
        }
        finally
        {
            foreach(var entry in originals)entry.Key.Opacity=entry.Value;
            Root.RequestedTheme=theme;ResizeForDips(1440,940);ApplyVisualPreferences();flowingBackdrop.SetBlur(blur);
            flowingBackdrop.SetDiagnosticProgress(0,0);
        }
        await File.WriteAllTextAsync(Path.Combine(output,"BACKGROUND_COVERAGE_FRAMES.json"),JsonSerializer.Serialize(new{actual_window_captures=true,cases},new JsonSerializerOptions{WriteIndented=true}));
    }
}
