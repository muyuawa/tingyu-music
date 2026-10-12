using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
using Windows.UI;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private AlbumPalette albumColors=AlbumColorEngine.Fallback;
    private readonly Dictionary<string,SolidColorBrush> sceneBrushes=[];
    private bool sceneResourcesPresent=true;
    private static readonly string[] accentAliases=["SliderThumbBackground","SliderThumbBackgroundPointerOver","SliderThumbBackgroundPressed","SliderTrackValueFill","SliderTrackValueFillPointerOver","SliderTrackValueFillPressed","ListViewItemSelectionIndicatorBrush","ListViewItemSelectionIndicatorPointerOverBrush","ListViewItemSelectionIndicatorPressedBrush"];
    private AlbumScene? activeColorScene,targetColorScene;
    private Color[]? albumPalette
    {
        get=>albumColors.HasArtwork?albumColors.Colors.Select(ToColor).ToArray():null;
        set=>albumColors=value==null?AlbumColorEngine.Fallback:AlbumColorEngine.Analyze(value.SelectMany(c=>new byte[]{c.R,c.G,c.B,255}).ToArray());
    }
    private bool EffectiveDark=>Root.ActualTheme==ElementTheme.Dark;
    private ElementTheme EffectiveTheme=>EffectiveDark?ElementTheme.Dark:ElementTheme.Light;
    private static Color ToColor(RgbColor c)=>Color.FromArgb(255,c.R,c.G,c.B);
    private static RgbColor ToRgb(Color c)=>new(c.R,c.G,c.B);
    private static Color Blend(Color a,Color b,double amount)=>ToColor(AlbumColorEngine.Blend(ToRgb(a),ToRgb(b),amount));
    private Color SavedAccent()
    {
        string value=(store.Get("accent-color")??"#245FCB").TrimStart('#');
        return uint.TryParse(value,System.Globalization.NumberStyles.HexNumber,null,out uint rgb)?Color.FromArgb(255,(byte)(rgb>>16),(byte)(rgb>>8),(byte)rgb):ToColor(AlbumColorEngine.Blue);
    }
    private void SetColorResource(string key,Color color)
    {
        var brush=sceneBrushes[key];brush.Color=color;
        if(!sceneResourcesPresent)Root.Resources[key]=brush;
    }
    private void InitializeSceneResources()
    {
        foreach(string key in new[]{"MuyuAccentBrush","AccentFillColorDefaultBrush","AccentFillColorSecondaryBrush","AccentFillColorTertiaryBrush","TextFillColorPrimaryBrush","TextFillColorSecondaryBrush","TextFillColorTertiaryBrush","TextControlPlaceholderForeground","TextControlForeground","AccentTextFillColorPrimaryBrush"}.Concat(accentAliases))
            sceneBrushes[key]=(SolidColorBrush)Root.Resources[key];
    }
    private void ApplyAccent(string? mode=null,Color? chosen=null)
    {
        bool custom=(mode??store.Get("accent-mode")??"album")=="custom";
        var target=AlbumColorEngine.Scene(albumColors,Root.ActualTheme==ElementTheme.Dark,page=="now",custom?ToRgb(chosen??SavedAccent()):null);
        bool same=targetColorScene is {} old&&ReferenceEquals(old.ArtworkRgba,target.ArtworkRgba)&&old.Dark==target.Dark&&old.Canvas==target.Canvas&&old.Accent==target.Accent&&old.Fields.SequenceEqual(target.Fields)&&old.FieldAlpha.SequenceEqual(target.FieldAlpha);
        var scene=same&&activeColorScene!=null?activeColorScene with{Playing=target.Playing}:AlbumColorEngine.GuardTransition(targetColorScene,target);
        targetColorScene=target;
        activeColorScene=scene;
        if(IsHighContrast)
        {
            foreach(string key in new[]{"MuyuAccentBrush","AccentFillColorDefaultBrush","AccentFillColorSecondaryBrush","AccentFillColorTertiaryBrush","SystemAccentColor","TextFillColorPrimaryBrush","TextFillColorSecondaryBrush","TextFillColorTertiaryBrush","TextControlPlaceholderForeground","TextControlForeground","AccentTextFillColorPrimaryBrush"})Root.Resources.Remove(key);
            foreach(string key in accentAliases)Root.Resources.Remove(key);
            sceneResourcesPresent=false;
            foreach(var region in new FrameworkElement[]{ContentRegion,TransportSurface,PopoverLayer,HeaderActions,ExitPlayerButton,NavigationHost,BrandPanel,SearchBox})region.RequestedTheme=ElementTheme.Default;
            Root.Background=new SolidColorBrush(new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background));
            Progress.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);VolumeSlider.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
            LyricsCanvas.SetColorScene(null);return;
        }
        foreach(var region in new FrameworkElement[]{ContentRegion,TransportSurface,PopoverLayer,HeaderActions,ExitPlayerButton,NavigationHost,BrandPanel,SearchBox})region.RequestedTheme=scene.Dark?ElementTheme.Dark:ElementTheme.Light;
        var accent=ToColor(scene.Accent);
        foreach(string key in new[]{"MuyuAccentBrush","AccentFillColorDefaultBrush","AccentFillColorSecondaryBrush","AccentFillColorTertiaryBrush"})SetColorResource(key,accent);
        foreach(string key in accentAliases)SetColorResource(key,accent);
        Root.Resources["SystemAccentColor"]=accent;
        var onAccent=AlbumColorEngine.Contrast(AlbumColorEngine.LightText,scene.Accent)>=4.5?AlbumColorEngine.LightText:AlbumColorEngine.DarkText;
        SetColorResource("TextFillColorPrimaryBrush",ToColor(scene.Text));
        foreach(string key in new[]{"TextFillColorSecondaryBrush","TextFillColorTertiaryBrush","TextControlPlaceholderForeground","TextControlForeground"})SetColorResource(key,ToColor(scene.SecondaryText));
        SetColorResource("AccentTextFillColorPrimaryBrush",ToColor(onAccent));
        sceneResourcesPresent=true;
        Root.Background=new SolidColorBrush(ToColor(scene.Canvas));
        Progress.Foreground=VolumeSlider.Foreground=new SolidColorBrush(accent);
        LyricsCanvas.SetColorScene(scene);
        flowingBackdrop?.SetScene(scene);
        glassRenderer?.SetAccent(accent);
        artworkElevation?.SetArtworkStyle(albumColors.IsDark,albumColors.IsBright,ToColor(scene.Accent));
    }
    private static Color[] ExtractPalette(byte[] pixels)=>AlbumColorEngine.Analyze(pixels).Colors.Select(ToColor).ToArray();
}
