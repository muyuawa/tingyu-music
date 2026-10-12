using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using System.Text.Json;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureBackgroundBlur(string output)
    {
        byte[] pixels=new byte[4096];
        RgbColor[] colors=[new(20,210,240),new(230,30,180),new(245,160,30),new(50,60,210)];
        for(int i=0;i<1024;i++){var c=colors[(i/32<16?0:2)+(i%32<16?0:1)];pixels[4*i]=c.R;pixels[4*i+1]=c.G;pixels[4*i+2]=c.B;pixels[4*i+3]=255;}
        albumColors=AlbumColorEngine.Analyze(pixels);
        captureFlowFrozen=true;
        Navigate("now");await Refresh();
        var originals=Root.Children.ToDictionary(child=>child,child=>child.Opacity);
        foreach(var child in Root.Children)if(child!=AtmosphereHost)child.Opacity=0;
        var records=new List<object>();
        foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark})
        foreach(int width in new[]{1440,960})
        {
            Root.RequestedTheme=theme;ResizeForDips(width,940);ApplyVisualPreferences();
            flowingBackdrop!.SetForeground(false);flowingBackdrop.SetDiagnosticProgress(.15f,.35f);
            await Task.Delay(1000);
            foreach(double amount in new[]{0d,40d,80d})
            {
                flowingBackdrop.SetBlur(amount);await Task.Delay(400);
                await WindowCapture.Save(this,Path.Combine(output,$"blur-{theme}-{width}-{amount}.png"),2);
                records.Add(new{theme=theme.ToString(),width,requested=amount,applied=flowingBackdrop.BlurAmount});
            }
            flowingBackdrop.Configure(theme==ElementTheme.Dark,true,true,true,80);
            await Task.Delay(400);await WindowCapture.Save(this,Path.Combine(output,$"blur-{theme}-{width}-economy80.png"),2);
            if(flowingBackdrop.BlurAmount!=80)throw new InvalidOperationException("Economy mode capped background blur");
        }
        foreach(var original in originals)original.Key.Opacity=original.Value;
        store.Set("glass-economy","true");
        foreach(string target in new[]{"home","now"})
        {
            Navigate(target);await Refresh();ApplyVisualPreferences();
            double saved=flowingBackdrop!.BlurAmount;var glass=glassRenderer!.Parameters;
            bool lyricsBlur=LyricsCanvas.BlurEnabled;
            Settings_Click(this,new());
            for(int attempt=0;openSettings==null&&attempt<30;attempt++)await Task.Delay(100);
            if(openSettings is not {} settings || settings.Content is not ScrollViewer scroll || scroll.Content is not StackPanel options)throw new InvalidOperationException("Settings did not open");
            var background=options.Children.OfType<Expander>().First(e=>Equals(e.Header,"播放背景"));
            var slider=((StackPanel)background.Content).Children.OfType<Slider>().Single();
            foreach(double amount in new[]{0d,40d,80d}){slider.Value=amount;await Task.Delay(50);if(flowingBackdrop.BlurAmount!=amount)throw new InvalidOperationException("Settings preview did not reach renderer");}
            settings.Hide();for(int attempt=0;openSettings!=null&&attempt<30;attempt++)await Task.Delay(100);
            if(flowingBackdrop.BlurAmount!=saved||glassRenderer.Parameters!=glass||LyricsCanvas.BlurEnabled!=lyricsBlur)throw new InvalidOperationException("Cancel did not restore independent settings");
        }
        await File.WriteAllTextAsync(Path.Combine(output,"BACKGROUND_BLUR_VERIFICATION.json"),JsonSerializer.Serialize(new{pass=true,actual_window_capture=true,audio_opened=false,personal_data_accessed=false,economy_preserves_full_range=true,library_and_player_preview_and_cancel=true,glass_and_lyrics_settings_preserved=true,cases=records},new JsonSerializerOptions{WriteIndented=true}));
    }
}
