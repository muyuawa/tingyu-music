using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Rectangle=Microsoft.UI.Xaml.Shapes.Rectangle;
using Muyu.Core;
using System.Text.Json;
using Windows.UI;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private bool captureActive,captureFlowFrozen;
    private async Task CaptureIntegratedUi(string destination)
    {
        string output=Path.GetFullPath(destination);Directory.CreateDirectory(output);
        try
        {
            captureActive=true;Root.IsHitTestVisible=false;clock.Stop();volumePoll.Stop();Notice.IsOpen=false;
            AppWindow.Show();Activate();
            ResizeForDips(1440,940);await Task.Delay(500);
            if(Environment.GetCommandLineArgs().Contains("--queue-only")){await VerifyQueueScrolling(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--background-blur-only")){await CaptureBackgroundBlur(output);return;}
            Navigate("now");await Refresh();Layout();
            if(Environment.GetCommandLineArgs().Contains("--caption-only")){await CaptureVolumeBackendSwitch(output);await CaptureCaptionTransitions(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--obsolete-caption-only")){await CaptureCaptionTransitions(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--runtime-only")){await CaptureRuntimeStability(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--sort-only")){await CaptureTrackSorting(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--metadata-only")){await CaptureArtworkMetadata(output);return;}
            if(Environment.GetCommandLineArgs().Contains("--gloss-only")){await CaptureGlassGloss(output);return;}
            var captureArguments=Environment.GetCommandLineArgs();int artworkAt=Array.IndexOf(captureArguments,"--artwork");
            if(artworkAt>=0&&artworkAt+1<captureArguments.Length){await CaptureArtworkBackground(output,captureArguments[artworkAt+1]);return;}
            SongTitle.Text=MiniTitle.Text="暮色之间";SongArtist.Text="Muyu Ensemble  ·  静谧海岸";MiniArtist.Text="Muyu Ensemble";SongFormat.Text="视觉预览 · 未建立音频输出";
            NoLyrics.Visibility=Visibility.Collapsed;
            LyricsCanvas.Load(new Lyrics(new[]{new LyricLine(0,"把喧嚣留在身后"),new LyricLine(10,"让晚风慢慢经过"),new LyricLine(20,"沿着海岸走向夜色"),new LyricLine(30,"每个音符都有回声")},true,0,null));
            LyricsCanvas.Update(12,0);LyricsCanvas.SetPlaying(false);
            string cover=Path.Combine(output,"preview-cover.svg");
            await File.WriteAllTextAsync(cover,"<svg xmlns='http://www.w3.org/2000/svg' width='500' height='500' viewBox='0 0 500 500'><defs><linearGradient id='s' x2='0' y2='1'><stop stop-color='#f7cfa4'/><stop offset='0.5' stop-color='#7f9fbe'/><stop offset='1' stop-color='#233f61'/></linearGradient></defs><rect width='500' height='500' fill='url(#s)'/><path d='M0 310L90 180 175 270 260 160 410 295 500 220V500H0Z' fill='#3b5d80'/><path d='M0 380L130 255 210 345 320 240 500 340V500H0Z' fill='#1e3c59'/><path d='M0 420Q180 350 340 408T500 395V500H0Z' fill='#8aabc2'/><path d='M0 470Q130 375 270 440T500 450V500H0Z' fill='#1b344c'/></svg>");
            CoverImage.Source=new SvgImageSource(new Uri(cover));
            var lyricsFollow=await CaptureLyricsFollowing(output);
            current=null;waitingToResume=false;activeLyricsBinding=null;presentedLyricsRevision=null;clearLyrics=false;
            lyrics=new Lyrics(new[]{new LyricLine(0,"把喧嚣留在身后"),new LyricLine(10,"让晚风慢慢经过"),new LyricLine(20,"沿着海岸走向夜色"),new LyricLine(30,"每个音符都有回声")},true,0,null);
            LyricsCanvas.Load(lyrics);LyricsCanvas.SetClear(false);LyricsCanvas.Update(12,0);
            SongTitle.Text=MiniTitle.Text="暮色之间";SongArtist.Text="Muyu Ensemble  ·  静谧海岸";MiniArtist.Text="Muyu Ensemble";SongFormat.Text="视觉预览 · 未建立音频输出";
            albumPalette=[Color.FromArgb(255,39,87,139),Color.FromArgb(255,199,143,104),Color.FromArgb(255,99,151,166),Color.FromArgb(255,90,72,133)];
            volumeMode="software";RefreshVolumeControls(-12);
            settingProgress=true;Progress.Maximum=120;Progress.Value=50;settingProgress=false;Elapsed.Text="0:50";Duration.Text="2:00";
            foreach(var theme in new[]{ElementTheme.Dark,ElementTheme.Light})
            {
                Root.RequestedTheme=theme;ApplyVisualPreferences();await Task.Delay(1100);
                await WindowCapture.Save(this,Path.Combine(output,theme.ToString().ToLowerInvariant()+"-player.png"));
            }
            Root.RequestedTheme=ElementTheme.Dark;ApplyVisualPreferences();
            flowingBackdrop?.SetForeground(true);
            var playerDrag=CaptureTitleBarHitTest(Root.ActualWidth*.75,24);
            var exitPoint=ExitPlayerButton.TransformToVisual(Root).TransformPoint(new(20,20));
            var exitHit=CaptureTitleBarHitTest(exitPoint.X,exitPoint.Y);
            for(int frame=0;frame<12;frame++){await Task.Delay(500);await WindowCapture.Save(this,Path.Combine(output,$"flow-{frame:00}.png"));}
            ApplyAccent("custom",Color.FromArgb(255,153,80,181));await Task.Delay(600);await WindowCapture.Save(this,Path.Combine(output,"custom-color-player.png"));ApplyVisualPreferences();
            RefreshVolumeControls(-12.3);Volume_Click(this,new());RefreshVolumeControls(-12.3);await Task.Delay(700);
            bool preciseVolume=VolumeSlider.Minimum==-40&&VolumeSlider.Maximum==12&&VolumeSlider.StepFrequency==.1&&popupVolumeSlider?.StepFrequency==.1&&popupVolumeInput?.SmallChange==.1&&popupVolumeValue?.Text.Contains("-12.3")==true;
            await WindowCapture.Save(this,Path.Combine(output,"volume-popover.png"));ClosePopover();
            await CommitMute(true);Volume_Click(this,new());await Task.Delay(400);
            bool muteRestoresGain=softwareMuted&&savedAudio.GainDb==0&&popupVolumeMute?.IsOn==true;
            await WindowCapture.Save(this,Path.Combine(output,"volume-muted.png"));ClosePopover();await CommitMute(false);
            muteRestoresGain=muteRestoresGain&&!softwareMuted&&savedAudio.GainDb==0;
            queue=Enumerable.Range(0,12).Select(i=>new QueueItem(Guid.NewGuid(),new Track($"preview-{i}",Path.Combine(output,$"preview-{i}.flac"),new[]{"暮色之间","晚风来信","静谧海岸","回声"}[i%4],"Muyu Ensemble","静谧海岸",44100,16,2,44100*240))).ToList();
            queue[10]=queue[10] with{Track=queue[7].Track};
            index=0;current=queue[0].Track;Queue_Click(this,new());await Task.Delay(700);await WindowCapture.Save(this,Path.Combine(output,"queue-popover.png"));
            await CaptureQueueWave(output);
            var originalIds=queue.Select(item=>item.ItemId).ToArray();Track? requestedTrack=null;
            queuePreviewStart=track=>{requestedTrack=track;current=track;return Task.CompletedTask;};
            await queueItemActivation!(queue[10],false);
            bool queueClickExactItem=index==10&&requestedTrack==queue[10].Track&&originalIds.SequenceEqual(queue.Select(item=>item.ItemId));
            await queueItemActivation!(queue[4],true);
            bool queueMultiSelectPreservesPlayback=index==10;
            queuePreviewStart=null;ClosePopover();current=null;queue.Clear();index=-1;
            ResizeForDips(780,720);await Task.Delay(800);Layout();await WindowCapture.Save(this,Path.Combine(output,"narrow-player.png"));
            ExitPlayer_Click(this,new());await Task.Delay(500);
            bool narrowReturn=page=="library"&&NavigationHost.Visibility==Visibility.Visible&&SidebarColumn.Width.Value==72&&SearchBox.Visibility==Visibility.Visible&&ExitPlayerButton.Visibility==Visibility.Collapsed;
            await WindowCapture.Save(this,Path.Combine(output,"return-to-library-narrow.png"));
            Navigate("now");await Task.Delay(400);
            bool playingSidebarHidden=NavigationHost.Visibility==Visibility.Collapsed&&SidebarColumn.Width.Value==0;
            bool narrowProgressThumbVisible=CaptureProgressThumbFits();
            ResizeForDips(1440,940);await Task.Delay(600);Layout();
            ExitPlayer_Click(this,new());await Task.Delay(500);
            bool wideReturn=page=="library"&&NavigationHost.Visibility==Visibility.Visible&&SidebarColumn.Width.Value==224&&SearchBox.Visibility==Visibility.Visible&&BrandPanel.Visibility==Visibility.Visible&&ExitPlayerButton.Visibility==Visibility.Collapsed;
            await WindowCapture.Save(this,Path.Combine(output,"return-to-library-wide.png"));
            Navigate("home");await Refresh();
            var homeGroups=Enumerable.Range(0,8).Select(i=>new MusicGroup(new[]{"最近播放","静谧海岸","晚风来信","回声"}[i%4],"视觉预览 · 示例专辑",CoverImage.Source,[])).ToList();
            Groups.ItemsSource=homeGroups;EmptyLibrary.Visibility=Visibility.Collapsed;PageSubtitle.Text="视觉预览 · 示例专辑";
            foreach(var theme in new[]{ElementTheme.Dark,ElementTheme.Light}){Root.RequestedTheme=theme;ApplyVisualPreferences();await Task.Delay(700);await WindowCapture.Save(this,Path.Combine(output,theme.ToString().ToLowerInvariant()+"-home.png"));}
            var homeDrag=CaptureTitleBarHitTest(248,24);
            bool homeBackdropVisible=AtmosphereHost.Visibility==Visibility.Visible;
            bool progressThumbVisible=narrowProgressThumbVisible&&CaptureProgressThumbFits();
            bool transportTrackHorizontal=TransportMetadata.Visibility==Visibility.Visible&&MiniTitle.Visibility==Visibility.Visible&&TransportCover.Source==CoverImage.Source&&CaptureBounds(TransportCover).Right<CaptureBounds(MiniTitle).Left&&Math.Abs((CaptureBounds(TransportCover).Top+CaptureBounds(TransportCover).Bottom)/2-(CaptureBounds(TransportSurface).Top+CaptureBounds(TransportSurface).Bottom)/2)<1;
            bool transportClearTint=TransportSurface.Background is LiquidGlassWinUI.LiquidGlassBrush clearTransport&&clearTransport.TintA==glassRenderer!.Parameters.TintOpacity;
            ApplyAccent("custom",Color.FromArgb(255,74,131,111));await Task.Delay(500);await WindowCapture.Save(this,Path.Combine(output,"custom-color-home.png"));
            ApplyVisualPreferences();var savedBlur=glassRenderer!.Parameters;var savedCanvas=((SolidColorBrush)Root.Background).Color;
            Navigate("now");await Refresh();Layout();await Task.Delay(500);
            double savedBackgroundBlur=flowingBackdrop!.BlurAmount;
            captureFlowFrozen=true;flowingBackdrop.SetForeground(false);await Task.Delay(650);
            flowingBackdrop.SetBlur(0);await Task.Delay(250);await WindowCapture.Save(this,Path.Combine(output,"player-background-blur-0.png"));
            flowingBackdrop.SetBlur(80);await Task.Delay(250);await WindowCapture.Save(this,Path.Combine(output,"player-background-blur-80.png"));
            bool backgroundBlurIndependent=flowingBackdrop.BlurAmount==80&&glassRenderer.Parameters==savedBlur;
            flowingBackdrop.SetBlur(savedBackgroundBlur);
            Settings_Click(this,new());await Task.Delay(400);
            bool settingsRestored=false;
            bool backgroundBlurPreview=false;
            if(openSettings is {} settings&&settings.Content is ScrollViewer scroll&&scroll.Content is StackPanel options)
            {
                var accent=options.Children.OfType<Expander>().First(e=>Equals(e.Header,"主色调"));accent.IsExpanded=true;
                var colors=(StackPanel)accent.Content;colors.Children.OfType<ComboBox>().First().SelectedIndex=1;var custom=colors.Children.OfType<Expander>().First();custom.IsExpanded=true;((ColorPicker)custom.Content).Color=Color.FromArgb(255,153,80,181);
                await Task.Delay(500);await WindowCapture.Save(this,Path.Combine(output,"settings-color.png"));accent.IsExpanded=false;
                var glass=options.Children.OfType<Expander>().First(e=>Equals(e.Header,"液态玻璃"));glass.IsExpanded=true;await Task.Delay(250);
                var background=options.Children.OfType<Expander>().First(e=>Equals(e.Header,"播放背景"));background.IsExpanded=true;
                var backgroundSlider=((StackPanel)background.Content).Children.OfType<Slider>().Single();backgroundSlider.Value=42.5;
                backgroundBlurPreview=flowingBackdrop.BlurAmount==42.5&&glassRenderer.Parameters==savedBlur; background.IsExpanded=false;
                var sliders=((StackPanel)glass.Content).Children.OfType<Slider>().ToArray();sliders[0].Value=6;sliders[1].Value=14;
                scroll.ChangeView(null,glass.TransformToVisual(options).TransformPoint(new(0,0)).Y,null,true);
                await Task.Delay(500);await WindowCapture.Save(this,Path.Combine(output,"settings-glass.png"));settings.Hide();
                for(int attempt=0;openSettings!=null&&attempt<30;attempt++)await Task.Delay(100);
                settingsRestored=glassRenderer.Parameters==savedBlur&&((SolidColorBrush)Root.Background).Color==savedCanvas&&flowingBackdrop.BlurAmount==savedBackgroundBlur;
            }
            bool visualPolish=await CaptureVisualPolish(output);
            bool contentOverlapsTransport=await CaptureTransportOverlap(output);
            contentOverlapsTransport&=await CaptureArtworkMetadata(output);
            bool albumColorVerified=await CaptureAlbumColors(output);
            albumColorVerified&=await CaptureGlassGloss(output);
            Navigate("now");await Refresh();Root.RequestedTheme=ElementTheme.Dark;Layout();await Task.Delay(600);
            captureFlowFrozen=true;flowingBackdrop?.SetForeground(false);
            bool playerTransportGlass=TransportSurface.Background is LiquidGlassWinUI.LiquidGlassBrush playerGlass&&playerGlass.IsUsingShader&&playerGlass.TintA==glassRenderer!.Parameters.TintOpacity&&transportElevation?.IsVisible==true;
            var pattern=new Canvas{IsHitTestVisible=false,Opacity=.85};Grid.SetRowSpan(pattern,3);Grid.SetColumnSpan(pattern,2);Root.Children.Insert(1,pattern);
            for(int x=-160;x<1800;x+=80){var line=new Rectangle{Width=34,Height=1000,Fill=new SolidColorBrush(x%160==0?Color.FromArgb(255,39,128,204):Color.FromArgb(255,229,161,105))};Canvas.SetLeft(line,x);pattern.Children.Add(line);}
            for(int y=0;y<1000;y+=60){var line=new Rectangle{Width=1900,Height=2,Fill=new SolidColorBrush(Color.FromArgb(190,245,250,255))};Canvas.SetTop(line,y);pattern.Children.Add(line);}
            var movement=new TranslateTransform();pattern.RenderTransform=movement;
            Volume_Click(this,new());await Task.Delay(400);
            var normalParameters=glassRenderer!.Parameters;
            glassRenderer.Parameters=normalParameters with{Refraction=1,Dispersion=0};ApplyVisualPreferences();await Task.Delay(500);
            await WindowCapture.Save(this,Path.Combine(output,"refraction-baseline.png"));
            glassRenderer.Parameters=normalParameters;ApplyVisualPreferences();await Task.Delay(500);
            for(int frame=0;frame<12;frame++){movement.X=frame*9;await Task.Delay(180);await WindowCapture.Save(this,Path.Combine(output,$"refraction-{frame:00}.png"));}
            movement.X=0;glassRenderer.Parameters=normalParameters with{Blur=0,OverlayBlur=0};ApplyVisualPreferences();await Task.Delay(400);await WindowCapture.Save(this,Path.Combine(output,"refraction-without-blur.png"));
            glassRenderer.Parameters=normalParameters;
            Root.Children.Remove(pattern);
            int shaders=glassRenderer?.ActiveShaderCount??0;
            var transport=FloatingSurface.TransformToVisual(Root).TransformPoint(new(0,0));
            await File.WriteAllTextAsync(Path.Combine(output,"UI_RENDER_VERIFICATION.json"),JsonSerializer.Serialize(new{version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),os=Environment.OSVersion.VersionString,actual_window_capture=true,nonblack_capture_required=true,audio_output_opened=false,music_files_accessed=false,shader_surfaces=shaders,taskbar_interface_created=taskbar!=null,media_control_available=media!=null,shader_error=glassRenderer?.Diagnostic,pass=albumColorVerified&&transportTrackHorizontal&&visualPolish&&contentOverlapsTransport&&homeBackdropVisible&&progressThumbVisible&&transportClearTint&&backgroundBlurIndependent&&backgroundBlurPreview&&shaders>=3&&glassRenderer?.Diagnostic==null&&playerDrag==2&&homeDrag==2&&exitHit==1&&settingsRestored&&wideReturn&&narrowReturn&&playingSidebarHidden&&lyricsFollow.Enter&&lyricsFollow.Track&&lyricsFollow.Clear&&preciseVolume&&muteRestoresGain&&queueClickExactItem&&queueMultiSelectPreservesPlayback&&playerTransportGlass,player_transport_uses_live_glass_with_frosted_body_and_soft_shadow=playerTransportGlass,transport_cover_and_song_title_horizontal=transportTrackHorizontal,transport_metadata_vertically_centered=transportTrackHorizontal,universal_color_engine_verified=albumColorVerified,visual_polish_verified=visualPolish,content_underlaps_live_transport_and_end_items_safe=contentOverlapsTransport,home_backdrop_visible=homeBackdropVisible,progress_thumb_fully_visible_wide_and_narrow=progressThumbVisible,transport_retains_configured_material_opacity=transportClearTint,playback_background_blur_independent=backgroundBlurIndependent,playback_background_blur_preview_and_cancel=backgroundBlurPreview&&settingsRestored,queue_click_handler_exact_item=queueClickExactItem,queue_multi_select_does_not_switch=queueMultiSelectPreservesPlayback,queue_route_uses_preview_instead_of_audio=true,library_transport_uses_live_glass=transportClearTint,settings_cancel_restored=settingsRestored,wide_return_restores_sidebar=wideReturn,narrow_return_restores_sidebar=narrowReturn,playing_hides_sidebar=playingSidebarHidden,lyrics_player_entry_follows=lyricsFollow.Enter,lyrics_new_track_follows=lyricsFollow.Track,lyrics_clear_text_can_follow=lyricsFollow.Clear,volume_range_and_tenth_db=preciseVolume,software_mute_preserves_gain=muteRestoresGain,frames=12,flow_frames=12,flow_interval_ms=500,player_caption_hit=playerDrag,home_caption_hit=homeDrag,return_button_hit=exitHit,dpi_scale=Root.XamlRoot.RasterizationScale,transport=new{x=transport.X,y=transport.Y,width=FloatingSurface.ActualWidth,height=FloatingSurface.ActualHeight}},new JsonSerializerOptions{WriteIndented=true}));
        }
        catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(output,"capture-error.txt"),ex.ToString());}
        finally{captureActive=false;Root.IsHitTestVisible=true;Close();}
    }
    private bool CaptureProgressThumbFits()
    {
        FrameworkElement? FindThumb(DependencyObject parent)
        {
            if(parent is FrameworkElement element&&element.Name=="HorizontalThumb")return element;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindThumb(VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}
            return null;
        }
        if(FindThumb(Progress) is not {} thumb||VisualTreeHelper.GetChildrenCount(thumb)==0||VisualTreeHelper.GetChild(thumb,0) is not FrameworkElement outer)return false;
        var top=outer.TransformToVisual(Progress).TransformPoint(new(0,0)).Y;
        return top>=0&&top+outer.ActualHeight<=Progress.ActualHeight;
    }
    private async Task<(bool Enter,bool Track,bool Clear)> CaptureLyricsFollowing(string output)
    {
        var sample=new Lyrics(Enumerable.Range(0,40).Select(i=>new LyricLine(i*3,$"示例段落 {i+1:00} · 当前句自动跟随")).ToArray(),true,0,null);
        var track=new Track("preview-follow",Path.Combine(output,"not-an-audio-file.flac"),"歌词跟随验证","Muyu Ensemble","视觉预览",44100,16,2,44100*120);
        Save("lyrics-binding:"+track.Id,new LyricsBindingHistory(new LyricsBinding(Path.Combine(output,"preview-follow.lrc"),"utf-8","preview-follow-revision",LyricsTimeDomain.Track,sample,0),[]));
        clearLyrics=true;LyricsCanvas.SetClear(true);
        PresentTrack(track);++generation;waitingToResume=true;resumeSeconds=52;UpdateLyricsTiming();await Task.Delay(500);
        bool newTrack=LyricsCanvas.IsFollowing&&LyricsCanvas.ActiveLine==17&&LyricsCanvas.IsCurrentLineAligned&&!clearLyrics;
        await WindowCapture.Save(this,Path.Combine(output,"lyrics-new-track-following.png"));
        clearLyrics=true;LyricsCanvas.SetClear(true);ExitPlayer_Click(this,new());await Task.Delay(150);
        Navigate("now");await Task.Delay(600);
        bool entry=LyricsCanvas.IsFollowing&&LyricsCanvas.ActiveLine==17&&LyricsCanvas.IsCurrentLineAligned&&!clearLyrics;
        await WindowCapture.Save(this,Path.Combine(output,"lyrics-enter-following.png"));
        bool oldBlur=LyricsCanvas.BlurEnabled;LyricsCanvas.BlurEnabled=false;LyricsCanvas.Update(61,0);await Task.Delay(500);
        bool clear=LyricsCanvas.IsFollowing&&LyricsCanvas.ActiveLine==20&&LyricsCanvas.IsCurrentLineAligned;
        await WindowCapture.Save(this,Path.Combine(output,"lyrics-clear-following.png"));LyricsCanvas.BlurEnabled=oldBlur;
        return(entry,newTrack,clear);
    }
    private int CaptureTitleBarHitTest(double x,double y,bool refreshRegions=true)
    {
        if(refreshRegions)UpdateTitleBarRegions();double scale=Root.XamlRoot.RasterizationScale;
        var point=new CapturePoint{X=(int)(x*scale),Y=(int)(y*scale)};var handle=WinRT.Interop.WindowNative.GetWindowHandle(this);CaptureClientToScreen(handle,ref point);
        return (int)CaptureSendMessage(handle,0x84,IntPtr.Zero,new IntPtr(unchecked((point.Y<<16)|(point.X&0xffff))));
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]private struct CapturePoint{public int X,Y;}
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="ClientToScreen")]private static extern bool CaptureClientToScreen(IntPtr handle,ref CapturePoint point);
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern IntPtr CaptureSendMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);
}
