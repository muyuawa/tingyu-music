using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private Window? desktopLyrics;
    private TextBlock? desktopLine;
    private string desktopText="";
    private void ToggleDesktopLyrics()
    {
        if(desktopLyrics!=null){desktopLyrics.Close();return;}
        desktopLyrics=new Window{Title="听屿 · 桌面歌词"};var grid=new Grid{Padding=new Thickness(20,12,20,12),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(255,28,28,34))};desktopLine=new TextBlock{Text="暂无同步歌词",FontSize=28,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,Foreground=new SolidColorBrush(Colors.White),TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,IsTextSelectionEnabled=true};grid.Children.Add(desktopLine);desktopLyrics.Content=grid;
        var menu=new MenuFlyout();ApplyGlassToMenu(menu);var top=new ToggleMenuFlyoutItem{Text="始终置顶",IsChecked=true};top.Click+=(_,_)=>{if(desktopLyrics?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)p.IsAlwaysOnTop=top.IsChecked;};menu.Items.Add(top);var close=new MenuFlyoutItem{Text="关闭桌面歌词"};close.Click+=(_,_)=>desktopLyrics?.Close();menu.Items.Add(close);grid.ContextFlyout=menu;
        if(desktopLyrics.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter){presenter.IsAlwaysOnTop=true;presenter.IsMaximizable=false;}
        double scale=Root.XamlRoot?.RasterizationScale??1;desktopLyrics.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(720*scale),(int)(150*scale)));desktopLyrics.Closed+=(_,_)=>{desktopLyrics=null;desktopLine=null;desktopText="";};UpdateDesktopLyrics();desktopLyrics.Activate();
    }
    private void UpdateDesktopLyrics(){if(desktopLine==null)return;int active=lyrics.ActiveIndex(DisplayPosition+lyricBase,lyricOffset);string text=active>=0?lyrics.Lines[active].Text:lyrics.Lines.Count==0?"暂无歌词":current?.Title??"等待播放";if(text==desktopText)return;desktopText=text;desktopLine.Text=text;}
}
