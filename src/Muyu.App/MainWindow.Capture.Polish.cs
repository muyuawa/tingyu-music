using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Text.Json;
using Windows.UI;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureQueueWave(string output)
    {
        FrameworkElement? FindMarker(DependencyObject parent)
        {
            if(parent is FrameworkElement element&&element.Name=="CurrentQueueMarker"&&element.Visibility==Visibility.Visible)return element;
            for(int i=0;i<Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
                if(FindMarker(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent,i)) is {} found)return found;
            return null;
        }
        var marker=FindMarker(FloatingContent)??throw new InvalidOperationException("Current queue marker was not realized");
        bool saved=captureFlowFrozen;captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        try
        {
            for(int i=0;i<6;i++){await Task.Delay(250);await WindowCapture.Save(this,Path.Combine(output,$"queue-wave-{i:00}.png"));}
            await File.WriteAllTextAsync(Path.Combine(output,"QUEUE_WAVE_VERIFICATION.json"),JsonSerializer.Serialize(new
            {
                marker_bounds=CaptureBounds(marker),dpi_scale=Root.XamlRoot.RasterizationScale,frames=6,interval_ms=250,
                background_frozen=true,description="decorative GPU playing indicator; not a measured audio-level meter",audio_output_opened=false
            },new JsonSerializerOptions{WriteIndented=true}));
        }
        finally{captureFlowFrozen=saved;flowingBackdrop?.SetForeground(!saved);}
    }
    private async Task<bool> CaptureVisualPolish(string output)
    {
        var pixels=new List<byte>();
        void Pixels(int count,byte r,byte g,byte b){for(int i=0;i<count;i++)pixels.AddRange([r,g,b,255]);}
        Pixels(800,12,12,12);Pixels(100,185,185,185);Pixels(100,250,250,250);Pixels(12,25,221,242);Pixels(10,183,55,231);
        var colors=ExtractPalette(pixels.ToArray());
        bool paletteVivid=colors.Any(c=>c.G>180&&c.B>180&&c.R<80)&&colors.Any(c=>c.R>140&&c.B>180&&c.G<100);
        string file=Path.Combine(output,"vivid-preview-cover.svg");
        await File.WriteAllTextAsync(file,"""
            <svg xmlns="http://www.w3.org/2000/svg" width="640" height="640" viewBox="0 0 640 640">
            <defs><linearGradient id="c" x2="1" y2="1"><stop stop-color="#19ddf2"/><stop offset=".55" stop-color="#625cff"/><stop offset="1" stop-color="#b737e7"/></linearGradient><radialGradient id="g"><stop stop-color="#382965"/><stop offset="1" stop-color="#10121e"/></radialGradient></defs>
            <rect width="640" height="640" fill="url(#g)"/><circle cx="320" cy="287" r="208" fill="none" stroke="url(#c)" stroke-width="55"/>
            <circle cx="320" cy="287" r="140" fill="none" stroke="#19ddf2" stroke-width="2" opacity=".6"/><path d="M72 370Q210 145 320 287T568 205" fill="none" stroke="url(#c)" stroke-width="18"/>
            <text x="54" y="553" font-family="Segoe UI" font-size="36" font-weight="600" fill="white">AFTERGLOW</text><text x="56" y="590" font-family="Segoe UI" font-size="15" letter-spacing="5" fill="#c6c7e2">MUYU · VISUAL PREVIEW</text></svg>
            """);
        CoverImage.Source=new SvgImageSource(new Uri(file));albumPalette=colors;
        Navigate("now");await Refresh();LyricsCanvas.Load(lyrics);LyricsCanvas.Update(12,0);LyricsCanvas.ResumeFollowing(true);
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        Root.RequestedTheme=ElementTheme.Light;ApplyVisualPreferences();await Task.Delay(1000);
        var island=CaptureBounds(TransportSurface);var cover=CaptureBounds(CoverFrame);
        bool geometry=island.Height==92&&island.Left>=48&&Root.ActualHeight-island.Bottom>=24&&TransportProgress.ActualWidth/island.Width is >=.5 and <=.65;
        bool fade=Math.Abs(LyricsCanvas.FadeEnd-LyricsCanvas.ActualHeight)<1;
        await WindowCapture.Save(this,Path.Combine(output,"vivid-light-player.png"));
        artworkElevation?.SetVisible(false);await Task.Delay(180);await WindowCapture.Save(this,Path.Combine(output,"artwork-shadow-off.png"));artworkElevation?.SetVisible(true);
        Root.RequestedTheme=ElementTheme.Dark;ApplyVisualPreferences();await Task.Delay(1000);await WindowCapture.Save(this,Path.Combine(output,"vivid-dark-player.png"));
        captureFlowFrozen=false;flowingBackdrop?.SetForeground(true);
        for(int i=0;i<12;i++){await Task.Delay(800);await WindowCapture.Save(this,Path.Combine(output,$"vivid-flow-{i:00}.png"));}
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        Volume_Click(this,new());await Task.Delay(400);
        bool volumeSimple=CaptureFindVisual<Expander>(FloatingContent) is {IsExpanded:false}&&popupVolumeValue?.Text.Contains("dB")==true;
        ClosePopover();
        bool pass=paletteVivid&&geometry&&fade&&volumeSimple;
        await File.WriteAllTextAsync(Path.Combine(output,"VISUAL_POLISH_VERIFICATION.json"),JsonSerializer.Serialize(new
        {
            pass,palette_preserves_small_cyan_and_violet_accents=paletteVivid,island_geometry_and_compact_progress=geometry,
            lyric_fade_extends_to_viewport_bottom=fade,volume_advanced_controls_collapsed=volumeSimple,
            island_bounds=island,cover_bounds=cover,dpi_scale=Root.XamlRoot.RasterizationScale,
            palette=colors.Select(c=>new{c.R,c.G,c.B}),flow_frames=12,flow_interval_ms=800,
            motion="smooth periodic compositor drift, 30–45 seconds; not a Brownian simulation",
            audio_output_opened=false,music_files_accessed=false,content="isolated example artwork and lyrics in actual player"
        },new JsonSerializerOptions{WriteIndented=true}));
        return pass;
    }
}
