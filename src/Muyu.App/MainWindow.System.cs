using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Muyu.Core;
using System.Runtime.InteropServices;
using Windows.Media;
using Windows.Storage.Streams;
using WinRT.Interop;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private SystemMediaTransportControls? media;
    private Window? miniWindow;
    private TextBlock? miniLabel;
    private Button? miniPlay;
    private bool explicitExit;
    private DateTime lastSystemUpdate;
    private ITaskbarList3? taskbar;
    private WindowSubclass? windowSubclass;
    private IntPtr trayIcon;
    private sealed record WindowState(int X,int Y,int Width,int Height,bool Maximized);
    private void InitializeSystemIntegration()
    {
        var hwnd=WindowNative.GetWindowHandle(this);
        try
        {
            media=SystemMediaTransportControlsInterop.GetForWindow(hwnd);media.IsEnabled=true;media.IsPlayEnabled=media.IsPauseEnabled=media.IsStopEnabled=media.IsNextEnabled=media.IsPreviousEnabled=true;
            media.ButtonPressed+=(_,args)=>DispatcherQueue.TryEnqueue(()=>{switch(args.Button){case SystemMediaTransportControlsButton.Play:if(audio.Status.State!=2)Play_Click(this,new());break;case SystemMediaTransportControlsButton.Pause:if(audio.Status.State==2)Play_Click(this,new());break;case SystemMediaTransportControlsButton.Stop:Stop_Click(this,new());break;case SystemMediaTransportControlsButton.Next:Next_Click(this,new());break;case SystemMediaTransportControlsButton.Previous:Previous_Click(this,new());break;}});
            media.PlaybackPositionChangeRequested+=(_,args)=>DispatcherQueue.TryEnqueue(async()=>await Seek(args.RequestedPlaybackPosition.TotalSeconds));
            taskbar=(ITaskbarList3)new TaskbarList();taskbar.HrInit();
        }catch(Exception ex){Show("系统媒体集成不可用："+ex.Message,true);}
        UpdateThumbBar();

        if(store.Get("tray")=="true")SetTray(true);
        AppWindow.Closing+=(_,args)=>{if(store.Get("tray")=="true"&&!explicitExit){args.Cancel=true;SaveWindowState();SetTray(true);AppWindow.Hide();}};
        if(Read<WindowState>("window-state") is {} state){var area=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;int width=Math.Clamp(state.Width,760,Math.Max(760,area.Width)),height=Math.Clamp(state.Height,620,Math.Max(620,area.Height));AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(Math.Clamp(state.X,area.X,Math.Max(area.X,area.X+area.Width-width)),Math.Clamp(state.Y,area.Y,Math.Max(area.Y,area.Y+area.Height-height)),width,height));if(state.Maximized&&AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)presenter.Maximize();}
    }
    private void SaveWindowState(){if(AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)Save("window-state",new WindowState(AppWindow.Position.X,AppWindow.Position.Y,AppWindow.Size.Width,AppWindow.Size.Height,presenter.State==Microsoft.UI.Windowing.OverlappedPresenterState.Maximized));}
    private void UpdateSystemMedia()
    {
        if(media==null||current==null)return;
        try{var updater=media.DisplayUpdater;updater.Type=MediaPlaybackType.Music;updater.MusicProperties.Title=current.Title;updater.MusicProperties.Artist=current.Artist;updater.MusicProperties.AlbumTitle=current.Album;updater.MusicProperties.AlbumArtist=current.AlbumArtist;updater.MusicProperties.TrackNumber=current.Number;updater.Update();_ = UpdateMediaCover(current,generation);}catch{}
    }
    private async Task UpdateMediaCover(Track track,int token){try{string cache=Path.Combine(dataFolder,"covers");var file=Directory.Exists(cache)?Directory.GetFiles(cache,Track.Identity(track.Path)+"*.img").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault():null;if(file==null||token!=generation||media==null)return;media.DisplayUpdater.Thumbnail=RandomAccessStreamReference.CreateFromFile(await Windows.Storage.StorageFile.GetFileFromPathAsync(file));media.DisplayUpdater.Update();}catch{}}
    private void UpdateSystemPosition()
    {
        if(DateTime.UtcNow-lastSystemUpdate<TimeSpan.FromSeconds(1))return;lastSystemUpdate=DateTime.UtcNow;
        try{if(media!=null){media.PlaybackStatus=waitingToResume&&current!=null?MediaPlaybackStatus.Paused:audio.Status.State switch{2=>MediaPlaybackStatus.Playing,1=>MediaPlaybackStatus.Paused,_=>MediaPlaybackStatus.Stopped};if(current!=null)media.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties{StartTime=TimeSpan.Zero,MinSeekTime=TimeSpan.Zero,Position=TimeSpan.FromSeconds(DisplayPosition),MaxSeekTime=TimeSpan.FromSeconds(current.Duration),EndTime=TimeSpan.FromSeconds(current.Duration)});}if(taskbar!=null&&taskbarReady){var hwnd=WindowNative.GetWindowHandle(this);taskbar.SetProgressState(hwnd,current==null?0u:audio.Status.State==2?2u:8u);if(current!=null)taskbar.SetProgressValue(hwnd,(ulong)(DisplayPosition*1000),(ulong)Math.Max(1,current.Duration*1000));}if(miniLabel!=null)miniLabel.Text=(current?.Title??"听屿")+"\n"+(current?.Artist??"本地音乐");if(miniPlay!=null)miniPlay.Content=audio.Status.State==2?"暂停":"播放";UpdateThumbBar();UpdateMini();}catch{}
    }
    private void SetTray(bool visible){var data=new NotifyIconData{Size=(uint)Marshal.SizeOf<NotifyIconData>(),Window=WindowNative.GetWindowHandle(this),Id=1,Flags=7,Callback=0x8000+44,Icon=trayIcon,Tip="听屿",Info="",InfoTitle=""};Shell_NotifyIcon(visible?0u:2u,ref data);}
    private IntPtr WindowProc(IntPtr hwnd,uint message,UIntPtr wParam,IntPtr lParam,UIntPtr id,UIntPtr data)
    {
        if(message is 0x001A or 0x031A)DispatcherQueue.TryEnqueue(()=>
        {
            if(closed)return;
            ApplyVisualPreferences();
            LyricsCanvas.BlurEnabled=store.Get("blur")!="false"&&store.Get("transparency")!="reduced"&&!IsHighContrast;
        });
        if(HandleShellMessage(message,wParam))return IntPtr.Zero;
        if(message==0x8000+44){int notification=(int)lParam&0xffff;if(notification==0x203){AppWindow.Show();Activate();return IntPtr.Zero;}if(notification==0x205){var menu=CreatePopupMenu();AppendMenu(menu,0,1,"显示 听屿");AppendMenu(menu,0,2,audio.Status.State==2?"暂停":"播放");AppendMenu(menu,0,3,"停止");AppendMenu(menu,0,4,"退出");GetCursorPos(out var point);SetForegroundWindow(hwnd);uint choice=TrackPopupMenu(menu,0x100|0x2,point.X,point.Y,0,hwnd,IntPtr.Zero);DestroyMenu(menu);if(choice==1){AppWindow.Show();Activate();}else if(choice==2)Play_Click(this,new());else if(choice==3)Stop_Click(this,new());else if(choice==4){explicitExit=true;Close();}return IntPtr.Zero;}}
        return DefSubclassProc(hwnd,message,wParam,lParam);
    }
    private void ApplyWindowsOptions(bool startup,bool associations,bool tray)
    {
        string exe=Environment.ProcessPath??throw new IOException("无法确定程序路径");
        using(var run=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")){if(startup)run.SetValue("MuyuMusic","\""+exe+"\" --background");else run.DeleteValue("MuyuMusic",false);}
        using var classes=Registry.CurrentUser.CreateSubKey(@"Software\Classes");
        if(associations){using(var prog=classes.CreateSubKey(@"MuyuMusic.Audio\shell\open\command"))prog.SetValue("","\""+exe+"\" --import \"%1\"");foreach(var ext in LibraryScanner.AudioExtensions.Concat(new[]{".cue",".m3u",".m3u8",".muyu-playlist"})){using var key=classes.CreateSubKey(ext+@"\OpenWithProgids");key.SetValue("MuyuMusic.Audio","",RegistryValueKind.String);}}
        else{foreach(var ext in LibraryScanner.AudioExtensions.Concat(new[]{".cue",".m3u",".m3u8",".muyu-playlist"})){using var key=classes.OpenSubKey(ext+@"\OpenWithProgids",true);key?.DeleteValue("MuyuMusic.Audio",false);}classes.DeleteSubKeyTree("MuyuMusic.Audio",false);}
        store.SetMany(new Dictionary<string,string>{["startup"]=startup?"true":"false",["associations"]=associations?"true":"false",["tray"]=tray?"true":"false"});SetTray(tray);SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);
    }
    private void CloseSystemIntegration(){SaveWindowState();SetTray(false);RemoveWindowSubclass(WindowNative.GetWindowHandle(this),windowSubclass!,1);if(media!=null){media.PlaybackStatus=MediaPlaybackStatus.Closed;media.IsEnabled=false;}miniWindow?.Close();desktopLyrics?.Close();foreach(var icon in shellIcons.Values.Where(h=>h!=IntPtr.Zero))DestroyIcon(icon);shellIcons.Clear();if(taskbar!=null){Marshal.ReleaseComObject(taskbar);taskbar=null;}}
    [ComImport,Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]private class TaskbarList{}
    [ComImport,Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]private interface ITaskbarList3{void HrInit();void AddTab(IntPtr h);void DeleteTab(IntPtr h);void ActivateTab(IntPtr h);void SetActiveAlt(IntPtr h);void MarkFullscreenWindow(IntPtr h,[MarshalAs(UnmanagedType.Bool)]bool fullscreen);void SetProgressValue(IntPtr h,ulong value,ulong total);void SetProgressState(IntPtr h,uint flags);void RegisterTab(IntPtr tab,IntPtr parent);void UnregisterTab(IntPtr tab);void SetTabOrder(IntPtr tab,IntPtr before);void SetTabActive(IntPtr tab,IntPtr parent,uint flags);void ThumbBarAddButtons(IntPtr h,uint count,[In]ThumbButton[] buttons);void ThumbBarUpdateButtons(IntPtr h,uint count,[In]ThumbButton[] buttons);void ThumbBarSetImageList(IntPtr h,IntPtr list);void SetOverlayIcon(IntPtr h,IntPtr icon,[MarshalAs(UnmanagedType.LPWStr)]string description);void SetThumbnailTooltip(IntPtr h,[MarshalAs(UnmanagedType.LPWStr)]string text);void SetThumbnailClip(IntPtr h,IntPtr rectangle);}
    private delegate IntPtr WindowSubclass(IntPtr h,uint m,UIntPtr w,IntPtr l,UIntPtr id,UIntPtr data);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct NotifyIconData{public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;public uint State,StateMask;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;public uint Timeout;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct HighContrastInfo{public uint Size,Flags;public IntPtr DefaultScheme;}
    private static bool IsHighContrast
    {
        get
        {
            var settings=new HighContrastInfo{Size=(uint)Marshal.SizeOf<HighContrastInfo>()};
            return SystemParametersInfo(0x0042,settings.Size,ref settings,0)&&(settings.Flags&1)!=0;
        }
    }
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action,uint parameter,ref HighContrastInfo settings,uint flags);
    [DllImport("comctl32.dll")]private static extern bool SetWindowSubclass(IntPtr h,WindowSubclass callback,uint id,UIntPtr data);
    [DllImport("comctl32.dll")]private static extern bool RemoveWindowSubclass(IntPtr h,WindowSubclass callback,uint id);
    [DllImport("comctl32.dll")]private static extern IntPtr DefSubclassProc(IntPtr h,uint m,UIntPtr w,IntPtr l);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern bool Shell_NotifyIcon(uint message,ref NotifyIconData data);
    [DllImport("shell32.dll")]private static extern void SHChangeNotify(uint eventId,uint flags,IntPtr first,IntPtr second);
    [DllImport("user32.dll")]private static extern IntPtr LoadIcon(IntPtr instance,IntPtr icon);
    [DllImport("user32.dll")]private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool AppendMenu(IntPtr menu,uint flags,uint id,string text);
    [DllImport("user32.dll")]private static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr owner,IntPtr rect);
    [DllImport("user32.dll")]private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr hwnd);
}
