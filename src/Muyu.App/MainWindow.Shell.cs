using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using WinRT.Interop;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private readonly uint taskbarCreated=RegisterWindowMessage("TaskbarButtonCreated");
    private bool taskbarReady,thumbsAdded,thumbsFailed;
    private readonly Dictionary<string,IntPtr> shellIcons=[];
    private Image? miniCover;
    private ProgressBar? miniProgress;
    private void InitializeWindowHooks()
    {
        var hwnd=WindowNative.GetWindowHandle(this);windowSubclass=WindowProc;SetWindowSubclass(hwnd,windowSubclass,1,UIntPtr.Zero);
        foreach(string name in new[]{"app","previous","play","pause","next"})shellIcons[name]=LoadImage(IntPtr.Zero,Path.Combine(AppContext.BaseDirectory,"Assets",name+".ico"),1,32,32,0x10);
        trayIcon=shellIcons.GetValueOrDefault("app");if(trayIcon==IntPtr.Zero)trayIcon=LoadIcon(IntPtr.Zero,new IntPtr(32512));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","app.ico"));
    }
    private void UpdateThumbBar()
    {
        if(taskbar==null||!taskbarReady||thumbsFailed)return;
        try{bool playing=audio.Status.State==2;var buttons=new[]{Thumb(101,"previous","上一首"),Thumb(102,playing?"pause":"play",playing?"暂停":"播放"),Thumb(103,"next","下一首")};if(thumbsAdded)taskbar.ThumbBarUpdateButtons(WindowNative.GetWindowHandle(this),3,buttons);else{taskbar.ThumbBarAddButtons(WindowNative.GetWindowHandle(this),3,buttons);thumbsAdded=true;}}catch{thumbsFailed=true;}
    }
    private ThumbButton Thumb(uint id,string icon,string title)=>new(){Mask=14,Id=id,Icon=shellIcons.GetValueOrDefault(icon),Tip=title,Flags=0};
    private bool HandleShellMessage(uint message,UIntPtr value)
    {
        if(message==taskbarCreated){taskbarReady=true;thumbsAdded=thumbsFailed=false;UpdateThumbBar();return true;}
        if(message==0x111&&((value.ToUInt64()>>16)&0xffff)==0x1800){switch(value.ToUInt64()&0xffff){case 101:Previous_Click(this,new());break;case 102:Play_Click(this,new());break;case 103:Next_Click(this,new());break;}return true;}return false;
    }
    private void Mini_Click(object sender,RoutedEventArgs e)
    {
        if(miniWindow!=null){miniWindow.Activate();return;}
        SaveWindowState();miniWindow=new Window{Title="听屿 · Mini"};
        var grid=new Grid{Padding=new Thickness(14),ColumnSpacing=12,RowSpacing=6,Background=new SolidColorBrush(Root.ActualTheme==ElementTheme.Dark?Windows.UI.Color.FromArgb(255,28,28,34):Windows.UI.Color.FromArgb(255,246,246,249)),RequestedTheme=Root.RequestedTheme};
        grid.PreviewKeyDown+=Root_ShortcutPreview;
        grid.PreviewKeyUp+=Root_ShortcutReleased;
        grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new(){Height=GridLength.Auto});
        miniCover=new Image{Source=CoverImage.Source,Width=56,Height=56,Stretch=Stretch.UniformToFill};Grid.SetRowSpan(miniCover,2);grid.Children.Add(miniCover);
        miniLabel=new TextBlock{Text=(current?.Title??"听屿")+"\n"+(current?.Artist??"本地音乐"),TextTrimming=TextTrimming.CharacterEllipsis};Grid.SetColumn(miniLabel,1);grid.Children.Add(miniLabel);
        var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};
        Button Add(string text,RoutedEventHandler action){var button=new Button{Content=text,Padding=new Thickness(8,3,8,3)};button.Click+=action;row.Children.Add(button);return button;}
        Add("上一首",Previous_Click);miniPlay=Add("播放",Play_Click);Add("下一首",Next_Click);
        var menu=new MenuFlyout();ApplyGlassToMenu(menu);var top=new ToggleMenuFlyoutItem{Text="始终置顶",IsChecked=store.Get("mini-top")=="true"};top.Click+=(_,_)=>{if(miniWindow?.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)p.IsAlwaysOnTop=top.IsChecked;store.Set("mini-top",top.IsChecked?"true":"false");};menu.Items.Add(top);var expand=new MenuFlyoutItem{Text="展开主窗口"};expand.Click+=(_,_)=>miniWindow?.Close();menu.Items.Add(expand);var lyricsItem=new MenuFlyoutItem{Text="桌面歌词"};lyricsItem.Click+=(_,_)=>ToggleDesktopLyrics();menu.Items.Add(lyricsItem);var more=new Button{Content="…",Flyout=menu,Padding=new Thickness(8,3,8,3)};row.Children.Add(more);Grid.SetColumn(row,1);Grid.SetRow(row,1);grid.Children.Add(row);
        miniProgress=new ProgressBar{Maximum=Math.Max(1,current?.Duration??1),Value=DisplayPosition,Height=3};Grid.SetRow(miniProgress,2);Grid.SetColumnSpan(miniProgress,2);grid.Children.Add(miniProgress);miniWindow.Content=grid;
        if(miniWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter){presenter.IsMaximizable=false;presenter.IsAlwaysOnTop=top.IsChecked;}
        double scale=Root.XamlRoot?.RasterizationScale??1;miniWindow.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(400*scale),(int)(160*scale)));miniWindow.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","app.ico"));
        miniWindow.Closed+=(_,_)=>{miniWindow=null;miniCover=null;miniLabel=null;miniPlay=null;miniProgress=null;if(!closed){AppWindow.Show();Activate();}};UpdateMini();miniWindow.Activate();AppWindow.Hide();
    }
    private void UpdateMini(){if(miniCover!=null)miniCover.Source=CoverImage.Source;if(miniProgress!=null){miniProgress.Maximum=Math.Max(1,current?.Duration??1);miniProgress.Value=DisplayPosition;}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ThumbButton{public uint Mask,Id,Bitmap;public IntPtr Icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Tip;public uint Flags;}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern IntPtr LoadImage(IntPtr instance,string file,uint type,int width,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyIcon(IntPtr icon);
}
