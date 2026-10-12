using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Muyu.Core;
using System.Text.Json;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private static FrameworkElement? CaptureNamed(DependencyObject parent,string name)
    {
        if(parent is FrameworkElement element&&element.Name==name)return element;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(CaptureNamed(VisualTreeHelper.GetChild(parent,i),name) is {} found)return found;
        return null;
    }
    private async Task<bool> CaptureAlbumColors(string output)
    {
        var oldPalette=albumColors;var oldTheme=Root.RequestedTheme;var oldCover=CoverImage.Source;
        string oldTitle=SongTitle.Text,oldArtist=SongArtist.Text,oldMini=MiniTitle.Text;
        bool oldFrozen=captureFlowFrozen;var cases=new List<object>();bool passed=true;
        var samples=new[]{("black","深暗封面",new RgbColor(0,0,0),new RgbColor(0,0,0)),("white","明亮封面",new RgbColor(255,255,255),new RgbColor(255,255,255)),("neon","青紫色封面",new RgbColor(25,221,242),new RgbColor(183,55,231)),("earth","低饱和封面",new RgbColor(144,129,113),new RgbColor(205,191,171)),("missing","无封面",new RgbColor(0,0,0),new RgbColor(0,0,0))};
        try
        {
            captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
            foreach(var (name,title,first,second) in samples)
            {
                byte[] pixels=Enumerable.Range(0,1024).SelectMany(i=>{var c=i%32<20?first:second;return new byte[]{c.R,c.G,c.B,255};}).ToArray();
                ImageSource? image=null;
                if(name!="missing")
                {
                    using var stream=new InMemoryRandomAccessStream();var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
                    encoder.SetPixelData(BitmapPixelFormat.Rgba8,BitmapAlphaMode.Straight,32,32,96,96,pixels);await encoder.FlushAsync();
                    stream.Seek(0);using var reader=new DataReader(stream);await reader.LoadAsync((uint)stream.Size);byte[] png=new byte[stream.Size];reader.ReadBytes(png);
                    string cover=Path.Combine(output,$"color-{name}-cover.png");await File.WriteAllBytesAsync(cover,png);image=new BitmapImage(new Uri(cover));
                }
                foreach(var global in new[]{ElementTheme.Light,ElementTheme.Dark})
                foreach(string target in new[]{"now","library"})
                {
                    Root.RequestedTheme=global;Navigate(target);await Refresh();
                    albumColors=name=="missing"?AlbumColorEngine.Fallback:AlbumColorEngine.Analyze(pixels);
                    CoverImage.Source=image;SongTitle.Text=MiniTitle.Text=title;SongArtist.Text="Muyu Music · 通用色彩验证";MiniArtist.Text="Muyu Music";SongFormat.Text="FLAC · 24-bit / 48 kHz";
                    NoLyrics.Visibility=Visibility.Collapsed;LyricsCanvas.Load(new Lyrics(Enumerable.Range(0,9).Select(i=>new LyricLine(i*10,new[]{"让封面的颜色成为此刻的舞台","每一种明暗都保留自己的轮廓","把声音留在光与影之间"}[i%3])).ToArray(),true,0,null));LyricsCanvas.Update(32,0);LyricsCanvas.ResumeFollowing(true);
                    if(target=="library")
                    {
                        TrackList.ItemsSource=Enumerable.Range(0,12).Select(i=>new Track($"color-{name}-{i}",Path.Combine(output,$"visual-only-{i}.flac"),$"{i+1:00} · {title} / 音乐与光","Muyu Ensemble","色彩与阅读",48000,24,2,48000*180)).ToArray();EmptyLibrary.Visibility=Visibility.Collapsed;
                    }
                    ApplyVisualPreferences();await Task.Delay(850);Root.UpdateLayout();
                    var scene=activeColorScene!;
                    RgbColor Read(Brush brush)=>brush is SolidColorBrush solid?ToRgb(solid.Color):new(255,0,255);
                    var primary=Read(target=="now"?SongTitle.Foreground:PageTitle.Foreground);var secondary=Read(target=="now"?SongArtist.Foreground:PageSubtitle.Foreground);
                    var fill=CaptureNamed(Progress,"HorizontalDecreaseRect") as Microsoft.UI.Xaml.Shapes.Rectangle;
                    var slider=fill==null?new RgbColor(255,0,255):Read(fill.Fill);
                    var thumb=CaptureNamed(Progress,"HorizontalThumb") as Control;
                    var thumbColor=thumb==null?new RgbColor(255,0,255):Read(thumb.Background);
                    bool inherited=primary==scene.Text&&secondary==scene.SecondaryText&&slider==scene.Accent&&thumbColor==scene.Accent;
                    bool themeScoped=Root.ActualTheme==global&&ContentRegion.ActualTheme==global&&scene.Dark==(global==ElementTheme.Dark);
                    bool contrast=scene.TextContrast>=4.5&&AlbumColorEngine.WorstContrast(secondary,scene.MinimumBackground,scene.MaximumBackground)>=4.5;
                    bool pass=inherited&&themeScoped&&contrast;passed&=pass;
                    string screenshot=$"color-{name}-{global.ToString().ToLowerInvariant()}-{(target=="now"?"player":"library")}.png";
                    await WindowCapture.Save(this,Path.Combine(output,screenshot));
                    cases.Add(new{name,page=target,global_theme=global.ToString(),effective_dark=scene.Dark,pass,theme_scoped_without_changing_global=themeScoped,actual_xaml_foregrounds_and_slider_inherit_colors=inherited,brightness=albumColors.AverageBrightness,saturation=albumColors.MeanSaturation,albumColors.IsDark,albumColors.IsBright,albumColors.IsMonochrome,dominant=albumColors.DominantColor.ToString(),vibrant=albumColors.VibrantColor.ToString(),deep=albumColors.DeepAmbientColor.ToString(),light=albumColors.LightAmbientColor.ToString(),canvas=scene.Canvas.ToString(),minimum_background=scene.MinimumBackground.ToString(),maximum_background=scene.MaximumBackground.ToString(),primary=primary.ToString(),expected_primary=scene.Text.ToString(),secondary=secondary.ToString(),expected_secondary=scene.SecondaryText.ToString(),slider=slider.ToString(),expected_accent=scene.Accent.ToString(),minimum_primary_contrast=scene.TextContrast,minimum_secondary_contrast=AlbumColorEngine.WorstContrast(secondary,scene.MinimumBackground,scene.MaximumBackground),minimum_lyric_opacity=scene.MinimumTextOpacity,screenshot});
                }
            }
            await File.WriteAllTextAsync(Path.Combine(output,"COLOR_ENGINE_VERIFICATION.json"),JsonSerializer.Serialize(new{pass=passed,cases,actual_integrated_window=true,artwork="synthetic 32 by 32 diagnostic swatches; not personal music",audio_output_opened=false,music_files_accessed=false,contrast_scope="authored text colors against the bounded ambient field; glyph antialiasing, intentional lyric edge fades, glass refraction of content and photographic artwork are not a whole-screen WCAG certification"},new JsonSerializerOptions{WriteIndented=true}));
            return passed;
        }
        finally
        {
            albumColors=oldPalette;Root.RequestedTheme=oldTheme;CoverImage.Source=oldCover;SongTitle.Text=oldTitle;SongArtist.Text=oldArtist;MiniTitle.Text=oldMini;
            captureFlowFrozen=oldFrozen;flowingBackdrop?.SetForeground(!oldFrozen);LyricsCanvas.Load(lyrics);LyricsCanvas.Update(12,0);ApplyVisualPreferences();
        }
    }
}
