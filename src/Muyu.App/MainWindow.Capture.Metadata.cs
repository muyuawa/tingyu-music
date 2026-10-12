using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Controls.Primitives;
using System.Text.Json;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private async Task<bool> CaptureArtworkMetadata(string output)
    {
        var args=Environment.GetCommandLineArgs();int artworkAt=Array.IndexOf(args,"--artwork");
        if(artworkAt>=0&&artworkAt+1<args.Length)CoverImage.Source=new BitmapImage(new Uri(Path.GetFullPath(args[artworkAt+1])));
        var cases=new List<object>();bool pass=true;
        captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
        foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
        foreach(var size in new[]{(1440,940),(1440,780),(1440,700),(1280,720),(1000,620),(916,620),(900,700),(884,620),(780,620)})
        foreach(int variant in Enumerable.Range(0,3))
        {
            Root.RequestedTheme=theme;
            ResizeForDips(size.Item1,size.Item2);await Task.Delay(180);
            SongTitle.Text=variant==0?"我是，女孩。":variant==1?"それを世界と言うんだね / とても長い曲名のレイアウトを確認":"晚风与海岸";
            SongArtist.Text=variant==2?"":"停電前夜 / Muyu Ensemble · 世界梦游";
            SongFormat.Text="FLAC · 16-bit / 44.1 kHz";
            Notice.IsOpen=variant==2;Notice.Message="视觉预览：验证提示出现时，完整歌曲资料仍然可见。";
            Layout();await Task.Delay(220);Root.UpdateLayout();
            var badge=CaptureBounds(QualityBadge);var text=CaptureBounds(SongFormat);var transport=CaptureBounds(TransportSurface);
            var ancestors=new List<object>();bool ancestorsContain=true,slotsContain=true;
            for(DependencyObject? parent=VisualTreeHelper.GetParent(QualityBadge);parent!=null;parent=VisualTreeHelper.GetParent(parent))
            {
                if(parent is not FrameworkElement element)continue;
                var bounds=CaptureBounds(element);
                var slot=LayoutInformation.GetLayoutSlot(element);
                ancestorsContain&=badge.Top>=bounds.Top-.5&&badge.Bottom<=bounds.Bottom+.5;
                bool slotFits=slot.Height+.5>=element.ActualHeight+element.Margin.Top+element.Margin.Bottom;
                slotsContain&=slotFits;
                ancestors.Add(new{name=element.Name,type=element.GetType().Name,bounds,desired=element.DesiredSize,slot,margin=element.Margin,slot_contains_content=slotFits});
                if(parent==Root)break;
            }
            bool padded=QualityBadge.ActualHeight+.5>=SongFormat.ActualHeight+QualityBadge.Padding.Top+QualityBadge.Padding.Bottom;
            bool visible=ancestorsContain&&slotsContain&&padded&&badge.Bottom<=transport.Top-8&&text.Bottom<=badge.Bottom-QualityBadge.Padding.Bottom+.5;
            pass&=visible;
            string screenshot=$"metadata-{theme.ToString().ToLowerInvariant()}-{size.Item1}x{size.Item2}-{variant}.png";
            await WindowCapture.Save(this,Path.Combine(output,screenshot));
            cases.Add(new{theme=theme.ToString(),width=size.Item1,height=size.Item2,variant,pass=visible,ancestors_contain_badge=ancestorsContain,layout_slots_contain_content=slotsContain,padding_complete=padded,badge,text,transport,ancestors,screenshot});
        }
        Notice.IsOpen=false;
        await File.WriteAllTextAsync(Path.Combine(output,"ARTWORK_METADATA_VERIFICATION.json"),JsonSerializer.Serialize(new{pass,version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),cases,actual_integrated_window=true,dpi_scale=Root.XamlRoot.RasterizationScale,application_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Muyu.App.dll")))),audio_output_opened=false,music_files_accessed=false},new JsonSerializerOptions{WriteIndented=true}));
        ResizeForDips(1440,940);await Task.Delay(200);Layout();
        return pass;
    }
}
