using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
using System.Text.Json;
using Windows.Foundation;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private static T? CaptureFindVisual<T>(DependencyObject parent) where T:DependencyObject
    {
        if(parent is T match)return match;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            if(CaptureFindVisual<T>(VisualTreeHelper.GetChild(parent,i)) is {} found)return found;
        return null;
    }

    private Rect CaptureBounds(FrameworkElement element)
    {
        var origin=element.TransformToVisual(Root).TransformPoint(new(0,0));
        return new(origin.X,origin.Y,element.ActualWidth,element.ActualHeight);
    }

    private async Task<bool> CaptureTransportOverlap(string output)
    {
        Navigate("library");await Refresh();Root.RequestedTheme=ElementTheme.Light;Layout();
        var tracks=Enumerable.Range(0,50).Select(i=>new Track($"under-glass-{i}",Path.Combine(output,$"visual-only-{i}.flac"),$"{i+1:00} · 晚风与海岸 / 浮在音乐之上","Muyu Ensemble · 仅界面示例","流动的专辑",44100,16,2,44100*120)).ToArray();
        TrackList.ItemsSource=tracks;EmptyLibrary.Visibility=Visibility.Collapsed;
        PageSubtitle.Text="视觉预览 · 列表从真实玻璃后方经过";
        await Task.Delay(400);
        var listScroll=CaptureFindVisual<ScrollViewer>(TrackList)??throw new InvalidOperationException("歌曲列表没有滚动视口");
        TrackList.ScrollIntoView(tracks[18],ScrollIntoViewAlignment.Leading);await Task.Delay(300);
        var anchor=(FrameworkElement?)TrackList.ContainerFromIndex(18)??throw new InvalidOperationException("示例歌曲未呈现");
        var transport=CaptureBounds(TransportSurface);
        listScroll.ChangeView(null,listScroll.VerticalOffset+CaptureBounds(anchor).Top-transport.Top-35,null,true);
        await Task.Delay(400);
        var underTrack=CaptureBounds(anchor);
        bool listExtends=CaptureBounds(TrackList).Bottom>transport.Bottom;
        bool trackUnderGlass=underTrack.Top<transport.Bottom&&underTrack.Bottom>transport.Top;
        await WindowCapture.Save(this,Path.Combine(output,"library-content-under-transport.png"));
        TransportSurface.Opacity=0;
        await Task.Delay(200);await WindowCapture.Save(this,Path.Combine(output,"library-without-transport-reference.png"));
        TransportSurface.Opacity=1;await Task.Delay(200);
        double start=listScroll.VerticalOffset;
        for(int i=0;i<8;i++)
        {
            listScroll.ChangeView(null,start+i*9,null,true);await Task.Delay(160);
            await WindowCapture.Save(this,Path.Combine(output,$"library-scroll-{i:00}.png"));
        }
        listScroll.ChangeView(null,double.MaxValue,null,true);await Task.Delay(400);
        var lastTrack=(FrameworkElement?)TrackList.ContainerFromIndex(tracks.Length-1)??throw new InvalidOperationException("最后一首示例歌曲未呈现");
        var lastTrackBounds=CaptureBounds(lastTrack);
        bool lastTrackClear=lastTrackBounds.Top>=CaptureBounds(TrackList).Top&&lastTrackBounds.Bottom<=transport.Top;
        await WindowCapture.Save(this,Path.Combine(output,"library-last-item-safe.png"));

        Navigate("home");await Refresh();
        var albums=Enumerable.Range(0,36).Select(i=>new MusicGroup($"{i+1:00} · 静谧海岸","视觉预览 · 示例专辑",CoverImage.Source,[])).ToArray();
        Groups.ItemsSource=albums;EmptyLibrary.Visibility=Visibility.Collapsed;await Task.Delay(350);
        var groupScroll=CaptureFindVisual<ScrollViewer>(Groups)??throw new InvalidOperationException("专辑没有滚动视口");
        Groups.ScrollIntoView(albums[18],ScrollIntoViewAlignment.Leading);await Task.Delay(300);
        var album=(FrameworkElement?)Groups.ContainerFromIndex(18)??throw new InvalidOperationException("示例专辑未呈现");
        groupScroll.ChangeView(null,groupScroll.VerticalOffset+CaptureBounds(album).Top-transport.Top-10,null,true);await Task.Delay(350);
        var underAlbum=CaptureBounds(album);
        bool albumUnderGlass=underAlbum.Top<transport.Bottom&&underAlbum.Bottom>transport.Top;
        bool albumCoverLoaded=CaptureFindVisual<Image>(album)?.Source!=null;
        await WindowCapture.Save(this,Path.Combine(output,"home-albums-under-transport.png"));
        groupScroll.ChangeView(null,double.MaxValue,null,true);await Task.Delay(350);
        var lastAlbum=(FrameworkElement?)Groups.ContainerFromIndex(albums.Length-1)??throw new InvalidOperationException("最后一个示例专辑未呈现");
        var lastAlbumBounds=CaptureBounds(lastAlbum);
        bool lastAlbumClear=lastAlbumBounds.Top>=CaptureBounds(Groups).Top&&lastAlbumBounds.Bottom<=transport.Top;
        await WindowCapture.Save(this,Path.Combine(output,"home-last-album-safe.png"));

        Navigate("now");await Refresh();NoLyrics.Visibility=Visibility.Collapsed;
        bool oldBlur=LyricsCanvas.BlurEnabled;LyricsCanvas.BlurEnabled=false;
        var sample=new Lyrics(Enumerable.Range(0,50).Select(i=>new LyricLine(i*3,$"{i+1:00} · 歌词经过玻璃，当前句保持清晰可见")).ToArray(),true,0,null);
        LyricsCanvas.Load(sample);LyricsCanvas.Update(60,0);LyricsCanvas.ResumeFollowing(true);await Task.Delay(350);
        var wideArtwork=CaptureBounds(ArtworkPanel);
        bool wideArtworkClear=wideArtwork.Bottom<=CaptureBounds(TransportSurface).Top;
        bool lyricsSafe=LyricsCanvas.IsCurrentLineAboveTransport&&CaptureBounds(LyricsPanel).Bottom>CaptureBounds(TransportSurface).Top;
        var lyricOrigin=CaptureBounds(LyricsCanvas);
        var playerTransport=CaptureBounds(TransportSurface);
        var underLyrics=LyricsCanvas.VisibleLinesBetween(playerTransport.Top-lyricOrigin.Top,playerTransport.Bottom-lyricOrigin.Top);
        bool glyphsUnderGlass=underLyrics.Any(row=>row.MaskAlpha>.2);
        bool qualitySafe=CaptureBounds(QualityBadge).Bottom<=playerTransport.Top&&CaptureBounds(SongFormat).Bottom<=CaptureBounds(QualityBadge).Bottom;
        await WindowCapture.Save(this,Path.Combine(output,"player-lyrics-under-transport.png"));
        TransportSurface.Opacity=TransportShadowHost.Opacity=0;await Task.Delay(180);
        await WindowCapture.Save(this,Path.Combine(output,"player-without-transport-reference.png"));
        TransportSurface.Opacity=TransportShadowHost.Opacity=1;
        for(int i=0;i<8;i++)
        {
            LyricsCanvas.Update(63+i*3,0);await Task.Delay(110);
            await WindowCapture.Save(this,Path.Combine(output,$"player-lyrics-scroll-{i:00}.png"));
        }
        ResizeForDips(780,620);await Task.Delay(500);Layout();LyricsCanvas.ResumeFollowing(true);await Task.Delay(200);
        var narrowArtwork=CaptureBounds(ArtworkPanel);
        bool narrowArtworkClear=narrowArtwork.Bottom<=CaptureBounds(TransportSurface).Top;
        lyricsSafe&=LyricsCanvas.IsCurrentLineAboveTransport;
        qualitySafe&=CaptureBounds(QualityBadge).Bottom<=CaptureBounds(TransportSurface).Top&&CaptureBounds(SongFormat).Bottom<=CaptureBounds(QualityBadge).Bottom;
        await WindowCapture.Save(this,Path.Combine(output,"minimum-player-safe.png"));

        bool pass=listExtends&&trackUnderGlass&&lastTrackClear&&albumUnderGlass&&albumCoverLoaded&&lastAlbumClear&&wideArtworkClear&&narrowArtworkClear&&lyricsSafe&&glyphsUnderGlass&&qualitySafe;
        await File.WriteAllTextAsync(Path.Combine(output,"TRANSPORT_OVERLAP_VERIFICATION.json"),JsonSerializer.Serialize(new
        {
            pass,list_viewport_extends_behind_transport=listExtends,track_row_inside_glass_bounds=trackUnderGlass,
            last_track_fully_above_transport=lastTrackClear,album_inside_glass_bounds=albumUnderGlass,last_album_fully_above_transport=lastAlbumClear,
            newly_realized_album_cover_loaded=albumCoverLoaded,
            wide_artwork_above_transport=wideArtworkClear,minimum_window_artwork_above_transport=narrowArtworkClear,
            lyrics_focus_above_transport_wide_and_minimum=lyricsSafe,scroll_frames=8,scroll_interval_ms=160,
            rendered_lyric_glyphs_under_glass=glyphsUnderGlass,under_glass_lyric_lines=underLyrics.Select(row=>new{line=row.Line,top=row.Top,bottom=row.Bottom,mask_alpha=row.MaskAlpha}),quality_badge_fully_visible=qualitySafe,lyric_scroll_frames=8,
            minimum_window_dips=new{width=780,height=620},transport_bounds=transport,under_track_bounds=underTrack,
            last_track_bounds=lastTrackBounds,under_album_bounds=underAlbum,last_album_bounds=lastAlbumBounds,
            audio_output_opened=false,music_files_accessed=false,content="isolated example tracks/albums/lyrics; actual integrated player"
        },new JsonSerializerOptions{WriteIndented=true}));
        LyricsCanvas.BlurEnabled=oldBlur;LyricsCanvas.Load(lyrics);LyricsCanvas.Update(12,0);
        ResizeForDips(1440,940);await Task.Delay(400);Layout();
        return pass;
    }
}
