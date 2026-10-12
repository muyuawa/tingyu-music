using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
using Windows.UI;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private SoftElevation? artworkElevation,transportElevation;
    private bool windowForeground=true;
    private bool visualsDisposed;
    private Action? afterSettingsClose;
    private void InitializeVisualPolish()
    {
        artworkElevation=new(ArtworkShadowHost,18,true);transportElevation=new(TransportShadowHost,46,false);
        foreach(var menu in new[]{LibraryMoreButton.Flyout,TransportMoreButton.Flyout}.OfType<MenuFlyout>())ApplyGlassToMenu(menu);
        Closed+=(_,_)=>{visualsDisposed=true;artworkElevation?.Dispose();transportElevation?.Dispose();};
    }
    private void RefreshVisualPolish(bool dark,bool reduced)
    {
        artworkElevation?.SetVisible(!IsHighContrast);ApplyTransportBackground();
        foreach(var pair in new[]{("TextControlBackground",dark?(byte)6:(byte)105),("TextControlBackgroundPointerOver",dark?(byte)12:(byte)145),("TextControlBackgroundFocused",dark?(byte)20:(byte)190)})
            SearchBox.Resources[pair.Item1]=new SolidColorBrush(IsHighContrast?((SolidColorBrush)Root.Background).Color:Color.FromArgb(pair.Item2,255,255,255));
        // AutoSuggestBox forwards its own Background to the inner TextBox.
        SearchBox.Background=(Brush)SearchBox.Resources["TextControlBackground"];
        SearchBox.Resources["TextControlCornerRadius"]=new CornerRadius(20);
        foreach(string key in new[]{"TextControlBorderBrush","TextControlBorderBrushPointerOver","TextControlBorderBrushFocused"})SearchBox.Resources[key]=new SolidColorBrush(IsHighContrast?Colors.Gray:Colors.Transparent);
        UpdateOutputBadge();
    }
    private void UpdateOutputBadge()
    {
        if(OutputBadgeText==null)return;
        var actual=audio.Actual;
        string next=actual==null?(waitingToResume?"待播放":"未建立输出")
            :$"{actual.Backend switch{AudioBackend.WasapiExclusive=>"独占",AudioBackend.WasapiShared=>"共享",_=>"ASIO"}} · {(actual.PcmModified?"已处理":savedAudio.Dsd==DsdMode.DoP?"DoP":"原始 PCM")}";
        if(OutputBadgeText.Text!=next)OutputBadgeText.Text=next;
        string tip=actual==null?next:$"{actual.OutputBits}-bit / {actual.OutputRate/1000.0:0.###} kHz · {actual.OutputChannels} 声道";
        if(!Equals(ToolTipService.GetToolTip(OutputBadge),tip))ToolTipService.SetToolTip(OutputBadge,tip);
    }
}
