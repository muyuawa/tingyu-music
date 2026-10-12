using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using Muyu.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Muyu.App;

public sealed record MusicGroup(string Name, string Detail, ImageSource? Cover, IReadOnlyList<Track> Tracks);

public sealed partial class MainWindow : Window
{
    private readonly LibraryStore store;
    private readonly AudioClient audio = new();
    private readonly SemaphoreSlim queueEdits = new(1);
    private readonly SemaphoreSlim seeks = new(1);
    private long seekRevision;
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource lifetime = new();
    private List<Track> library = [], visible = [];
    private List<QueueItem> queue = [];
    private int nextInsertion;
    private HashSet<string> favorites = [];
    private List<string> recent = [], folders = [];
    private Dictionary<string, List<string>> playlists = [];
    private int index = -1, repeat, generation;
    private bool busy, ticking, settingProgress, clearLyrics, shuffle, closed;
    private string page = "home", device = "";
    private bool shared;
    private Track? current;
    private Lyrics lyrics = Lyrics.Empty;
    private double lyricOffset, lyricBase;
    private double resumeSeconds;
    private readonly string dataFolder;
    private CancellationTokenSource? scan;
    private readonly Dictionary<string, ImageSource?> covers = [];
    public MainWindow()
    {
        InitializeComponent();
        InitializeSceneResources();
        dataFolder = Environment.GetEnvironmentVariable("MUYU_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuyuMusic");
        store = new LibraryStore(dataFolder);
        favorites = Read<HashSet<string>>("favorites") ?? [];
        recent = Read<List<string>>("recent") ?? [];
        folders = Read<List<string>>("folders") ?? [];
        playlists = Read<Dictionary<string, List<string>>>("playlists") ?? [];
        playlistTracks = Read<Dictionary<string, Track>>("playlist-track-snapshots") ?? [];
        LoadSmartPlaylists();
        if(int.TryParse(store.Get("library-sort"),out int savedSort)&&savedSort>=0&&savedSort<SortOrder.Items.Count)SortOrder.SelectedIndex=savedSort;
        globalLyricDelay = Read<double>("lyrics-global-delay");
        RestoreSessionState();
        device = store.Get("device") ?? ""; shared = store.Get("shared") == "true";
        InitializeAudioSettings();
        InitializeVolumeControls();
        InitializeProgressInteraction();
        InitializeShortcuts();InitializeLyricsExtensions();
        Root.RequestedTheme = Enum.TryParse<ElementTheme>(store.Get("theme"), out var theme) ? theme : ElementTheme.Default;
        LyricsCanvas.BlurEnabled = store.Get("blur") != "false";
        LyricsCanvas.ReduceMotion = store.Get("motion") == "reduced";
        if (IsHighContrast || store.Get("transparency") == "reduced") LyricsCanvas.BlurEnabled = false;
        LyricsCanvas.SeekRequested += async time => await SeekLyric(time);
        LyricsCanvas.FollowingResumed += () => clearLyrics = false;
        Root.SizeChanged += (_, _) => Layout();
        Root.ActualThemeChanged += (_, _) => ApplyVisualPreferences();
        Root.PreviewKeyDown+=Root_ShortcutPreview;
        Root.PreviewKeyUp+=Root_ShortcutReleased;
        Root.KeyDown+=Root_Keyboard;
        ApplyVisualPreferences();
        clock.Tick += async (_, _) => await Tick();
        Root.Loaded += async (_, _) =>
        {
            try
            {
                await ReloadLibraryState();
                Navigation.SelectedIndex = 0; await Refresh(); clock.Start();
                PresentRestoredSession(); ConfigureLibraryMonitoring();
                InitializeSystemIntegration();
                InitializePlayerGlass();
                ApplyVisualPreferences();
                await ReceiveActivation(Environment.GetCommandLineArgs(),true);
#if MUYU_DIAGNOSTICS
                var captureArgs=Environment.GetCommandLineArgs();int captureAt=Array.IndexOf(captureArgs,"--capture-ui");
                if(captureAt>=0&&captureAt+1<captureArgs.Length)await CaptureIntegratedUi(captureArgs[captureAt+1]);
#endif
            }
            catch (Exception ex) { Show(ex.Message, true); }
        };
        Closed += async (_, _) => await CloseSession();
        InitializeWindowHooks();
        ExtendsContentIntoTitleBar=true;
        // Explicit native regions own caption hit testing; do not also let SetTitleBar overwrite them.
        Root.LayoutUpdated+=(_,_)=>{if(!closed)UpdateTitleBarRegions();};
        ResizeForDips(1440,940,true);
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter) { double scale=GetDpiForWindow(WindowNative.GetWindowHandle(this))/96.0;presenter.PreferredMinimumWidth=(int)Math.Ceiling(760*scale);presenter.PreferredMinimumHeight=(int)Math.Ceiling(620*scale); }
    }
    private T? Read<T>(string key) { try { return JsonSerializer.Deserialize<T>(store.Get(key) ?? "null"); } catch (JsonException) { return default; } }
    private void Save<T>(string key, T value) => store.Set(key, JsonSerializer.Serialize(value));
    private void Show(string message, bool error = false) { if (closed) return; Notice.Message = message; Notice.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational; Notice.IsOpen = true; }
    private static string Time(double seconds) => TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    private async Task<bool> Command(AudioCommand command)
    {
        try { await audio.Send(command); return true; }
        catch (Exception ex) {
            Show(ex.Message, true);
            if(command.Action!="status")RecordAudioFailure(ex,command.Action,command.Track??current);
            return false;
        }
    }
    private async Task Start(Track track, bool showNow = false, double position = 0,bool autoPlay=true)
    {
        if (busy) return;
        busy = true;
        try
        {
            if (audio.Status.State == 2 && !await Command(new("pause"))) return;
            track = knownTracks.GetValueOrDefault(track.Id) ?? track;
            if (index >= 0 && index < queue.Count) queue[index] = queue[index] with { Track = track };
            PresentTrack(track); rememberedFrame = (long)Math.Clamp(Math.Round(position * track.SampleRate), 0, track.Frames); resumeSeconds = (double)rememberedFrame / track.SampleRate;
            waitingToResume = true; UpdateStoredPosition(); SaveQueue(false);
            if (showNow) Navigate("now");
            if (string.IsNullOrEmpty(device)||savedAudio.FollowSystemDefault)
            {
                if(savedAudio.Backend==AudioBackend.Asio){Show("请在音频设置中选择 ASIO 驱动",true);return;}
                var devices = await Task.Run(NativeAudio.Devices);
                device = devices.FirstOrDefault(d => d.IsDefault&&d.Available)?.Id ?? (savedAudio.FollowSystemDefault?"":devices.FirstOrDefault(d=>d.Available)?.Id) ?? "";
                if (device.Length == 0) { Show("没有可用的输出设备，请连接耳机或 DAC", true); return; }
                store.Set("device", device);
                savedAudio=savedAudio with{Device=device};Save("audio-settings",savedAudio);
            }
            var segments = PlaybackRun.FromQueue(queue, index, repeat != 1 && savedAudio.ReplayGain==ReplayGainMode.Off).TakeWhile(i=>AudioAuthorization.Kind(i.Track)==AudioAuthorization.Kind(track)).ToArray();
            var evidence=await Task.Run(()=>store.TrackFingerprints());
            bool approvedResume=exclusiveResumeGrant is {} grant&&grant.Track.Id==track.Id&&grant.Track.Path==track.Path&&grant.Track.StartFrame==track.StartFrame&&grant.Track.EndFrame==track.EndFrame&&grant.Settings.Device==savedAudio.Device&&grant.Settings.Revision==savedAudio.Revision&&grant.Settings.StrategyHash==savedAudio.StrategyHash;
            if(!approvedResume){exclusiveResumeGrant=null;if(!await AuthorizeAudio(track))return;}
            bool prepared = await Command(new("open", track, device, shared, Segments: segments,Settings:savedAudio,Evidence:segments.Select(i=>i.Track.Id).Distinct().Where(evidence.ContainsKey).ToDictionary(id=>id,id=>evidence[id])));
            if(!prepared&&await OfferCompatibleAudio(track))prepared=await Command(new("open",track,device,shared,Segments:segments,Settings:savedAudio,Evidence:segments.Select(i=>i.Track.Id).Distinct().Where(evidence.ContainsKey).ToDictionary(id=>id,id=>evidence[id])));
            if (prepared && rememberedFrame > 0) prepared = await Command(new("seek", Frame: rememberedFrame, ItemId: segments.FirstOrDefault()?.ItemId));
            if(prepared)exclusiveResumeGrant=null;
            if (prepared && volumeMode=="software" && softwareMuted) prepared = await Command(new("mute",Frame:1));
            if (prepared && (!autoPlay || await Command(new("play"))))
            {
                waitingToResume = false; SaveQueue();
                recent.Remove(track.Id); recent.Insert(0, track.Id); recent = recent.Take(100).ToList(); Save("recent", recent);
                store.RecordPlay(track.Id);UpdateSystemMedia();
                Notice.IsOpen = false;
            }
        }
        catch (Exception ex) { RecordAudioFailure(ex,"prepare",track);Show(ex.Message, true); }
        finally { busy = false; }
    }
    private void PresentTrack(Track track)
    {
        current = track; resumeSeconds = 0; rememberedFrame = 0; waitingToResume = false;
        presentedItemId = index >= 0 && index < queue.Count ? queue[index].ItemId : null;
        clearLyrics = false;
        activeLyricsBinding = ReadLyricsHistory(track)?.Current;
        ApplyBoundLyrics();
        LyricsCanvas.ResumeFollowing(true);
        SongTitle.Text = MiniTitle.Text = track.Title; SongArtist.Text = $"{track.Artist} · {track.Album}";
        MiniArtist.Text = track.Artist; SongFormat.Text = track.FormatText; Duration.Text = track.DurationText;
        settingProgress = true; Progress.Maximum = Math.Max(.001, track.Duration); Progress.Value = 0; settingProgress = false;
        UpdateFavorite(); ++generation;
        _ = LoadPresentation(track, generation);
    }
    private void SynchronizeCurrent()
    {
        if (audio.ItemId is not Guid id) return;
        int actual = queue.FindIndex(item => item.ItemId == id);
        if (actual < 0 || actual == index) return;
        Guid? previous=index>=0&&index<queue.Count?queue[index].ItemId:null;
        index = actual;CompleteDeferred(previous);nextInsertion = index + 1; PresentTrack(queue[index].Track); SaveQueue();
        recent.Remove(current!.Id); recent.Insert(0, current.Id); recent = recent.Take(100).ToList(); Save("recent", recent);
        store.RecordPlay(current.Id);UpdateSystemMedia();
    }
    private async Task EditQueue(Action edit)
    {
        await queueEdits.WaitAsync();
        try
        {
            if (busy || closed) return;
            busy = true;
            try
            {
                if (audio.ContinuousCue && !await Command(new("trim"))) return;
                SynchronizeCurrent();var before=queue.ToArray();edit();
                if(!before.Select(i=>i.ItemId).SequenceEqual(queue.Select(i=>i.ItemId))){queueStructureRevision++;if(!restoringQueue){queueUndo=new(queueStructureRevision,before,index>=0&&index<queue.Count?queue[index].ItemId:null,playedQueueIds.ToArray());ShowQueueUndo();}}
                if (index < 0 && queue.Count > 0) { index=queue.FindIndex(item=>!playedQueueIds.Contains(item.ItemId));nextInsertion=index+1; } SaveQueue();
            }
            finally { busy = false; }
        }
        finally { queueEdits.Release(); }
    }
    private async Task LoadPresentation(Track track, int token)
    {
        var cover = await Cover(track);
        if (token != generation || closed) return;
        CoverImage.Source = cover;
        await UpdateAtmosphere(track, token);
        try
        {
            await LoadLyrics(track, token);
        }
        catch (Exception ex) { Show("歌词未载入：" + ex.Message, true); }
    }
    private async Task<ImageSource?> Cover(Track track)
    {
        try
        {
            string key=await Task.Run(()=>CoverCache.Key(track.Path));if(covers.TryGetValue(key,out var existing))return existing;
            string? imagePath = await Task.Run(() =>
            {
                string cache = Path.Combine(dataFolder, "covers"); Directory.CreateDirectory(cache);
                string cached = Path.Combine(cache, key + ".img");
                if (File.Exists(cached)) return cached;
                var metadata = MetadataReader.Read(track.Path);
                if (metadata.Cover != null) { File.WriteAllBytes(cached, metadata.Cover);CoverCache.Trim(cache); return cached; }
                string dir = Path.GetDirectoryName(track.Path)!;
                return new[] { "cover.jpg", "folder.jpg", "cover.png", "folder.png" }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
            });
            BitmapImage? image = null;
            if (imagePath != null) { image = new BitmapImage { DecodePixelWidth = 640 }; using var stream = await FileRandomAccessStream(imagePath); await image.SetSourceAsync(stream); }
            if (covers.Count > 150) covers.Clear(); covers[key] = image; return image;
        }
        catch { return null; }
    }
    private static async Task<Windows.Storage.Streams.IRandomAccessStream> FileRandomAccessStream(string path) => await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
    private async Task Tick()
    {
        UpdateRenderActivity();
        if (ticking || busy || current == null || closed || waitingToResume) return;
        ticking = true;
        int pollGeneration = generation;
        try
        {
            if (!await Command(new("status"))) { SaveQueue(); waitingToResume = true; clock.Stop(); return; }
            if (busy || waitingToResume || pollGeneration != generation || closed) return;
            SynchronizeCurrent();
            var status = audio.Status;
            UpdateOutputBadge();
            PlayIcon.Glyph = status.State == 2 ? "\uE769" : "\uE768"; AutomationProperties.SetName(PlayButton, status.State == 2 ? "暂停" : "播放");
            settingProgress = true; Progress.Value = Math.Clamp(status.Seconds, 0, Progress.Maximum); settingProgress = false;
            Elapsed.Text = Time(status.Seconds);
            LyricsCanvas.SetPlaying(status.State == 2);
            UpdateLyricsTiming();
            CaptureReliablePosition();
            UpdateSystemPosition();
            if (DateTime.UtcNow - lastSessionSave >= TimeSpan.FromSeconds(30)) SaveQueue();
            if (status.State == 4) { SaveQueue(); Show(audio.Error, true); clock.Stop(); }
            else if (status.State == 3) await Advance(false);
        }
        finally { ticking = false; }
    }
    private async Task Seek(double seconds)
    {
        if (current == null || busy) return;
        if (waitingToResume)
        {
            rememberedFrame = (long)Math.Clamp(Math.Round(seconds * current.SampleRate), 0, current.Frames); resumeSeconds = (double)rememberedFrame / current.SampleRate;
            UpdateStoredPosition(); SaveQueue(); return;
        }
        var track = current;
        Guid? target = index >= 0 && index < queue.Count ? queue[index].ItemId : null;
        long revision = ++seekRevision;
        await seeks.WaitAsync();
        try
        {
            Guid? actual = index >= 0 && index < queue.Count ? queue[index].ItemId : null;
            if (revision != seekRevision || busy || closed || current != track || actual != target) return;
            await Command(new("seek", Frame: (long)(Math.Clamp(seconds, 0, track.Duration) * track.SampleRate), ItemId: target));
            SaveQueue();
        }
        finally { seeks.Release(); }
    }
    private void ReplaceQueue(IEnumerable<Track> tracks, int start = 0) {queueStructureRevision++;queueUndo=null;playedQueueIds.Clear();removeAfterPlay.Clear();queue = tracks.Select(t => new QueueItem(Guid.NewGuid(), t)).ToList(); index = queue.Count > 0 ? Math.Clamp(start, 0, queue.Count - 1) : -1; nextInsertion = index + 1; }
    private async Task Advance(bool manual)
    {
        if (queue.Count == 0) return;
        if (manual && !await RefreshCurrent()) return;
        if (!manual && repeat == 1 && current != null && !removeAfterPlay.Contains(queue[index].ItemId)) { await Start(current); return; }
        int next = index + 1;
        if (next >= queue.Count) { if (repeat == 2) next = 0; else { await Command(new("stop"));if(index>=0&&removeAfterPlay.Contains(queue[index].ItemId)){Guid finishedId=queue[index].ItemId;CompleteDeferred(finishedId);index=-1;current=null;waitingToResume=false;SongTitle.Text=MiniTitle.Text="播放结束";}else{waitingToResume=current!=null;rememberedFrame=0;resumeSeconds=0;UpdateStoredPosition();}SaveQueue(false);clock.Stop();PlayIcon.Glyph = "\uE768";return; } }
        Guid? previous=index>=0&&index<queue.Count?queue[index].ItemId:null;index = next;CompleteDeferred(previous);nextInsertion = index + 1; await Start(queue[index].Track);
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        clock.Start();
        if (waitingToResume && current != null) await Start(current, position: resumeSeconds);
        else if (audio.Status.State == 2) await PausePlayback();
        else if (audio.Status.State == 1) await Command(new("play"));
        else if (current != null) await Start(current, position: resumeSeconds);
        else if (queue.Count > 0) {index=queue.FindIndex(item=>!playedQueueIds.Contains(item.ItemId));if(index<0){Show("队列已播放完毕，可从音乐库重新选择歌曲");return;}nextInsertion = index + 1; await Start(queue[index].Track); }
        else if (visible.Count > 0) { ReplaceQueue(visible); await Start(queue[0].Track); }
    }
    private async void Next_Click(object sender, RoutedEventArgs e) { if (!busy) { clock.Start(); await Advance(true); } }
    private (Track Track,AudioSettings Settings)? exclusiveResumeGrant;
    private async Task PausePlayback()
    {
        if(busy)return;
        busy=true;
        try
        {
            var backend=audio.Actual?.Backend??savedAudio.Backend;
            if(!await PauseOutput.Execute(backend,action=>Command(new(action)),
                ()=>{SynchronizeCurrent();CaptureReliablePosition();SaveQueue(false);if(backend==AudioBackend.WasapiExclusive&&current!=null)exclusiveResumeGrant=(current,savedAudio);},
                ()=>{waitingToResume=current!=null;UpdateStoredPosition();}))return;
            LyricsCanvas.SetPlaying(false);clock.Stop();PlayIcon.Glyph="\uE768";
            AutomationProperties.SetName(PlayButton,"播放");lastSystemUpdate=default;UpdateSystemPosition();
        }
        finally{busy=false;}
    }
    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return; clock.Start();
        if (!await RefreshCurrent()) return;
        if (DisplayPosition > 3) { await Seek(0); return; }
        if (queue.Count > 0) { index = Math.Max(0, index - 1); nextInsertion = index + 1; await Start(queue[index].Track); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) { if (!busy) { if (!waitingToResume) await Command(new("stop")); waitingToResume = current != null; resumeSeconds = 0; rememberedFrame = 0; UpdateStoredPosition(); SaveQueue(); PlayIcon.Glyph = "\uE768"; } }
    private async Task<bool> RefreshCurrent()
    {
        if (!audio.ContinuousCue) return true;
        if (busy) return false;
        busy = true;
        try { if (!await Command(new("status"))) return false; SynchronizeCurrent(); return true; }
        finally { busy = false; }
    }
    private void Progress_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (!settingProgress && current != null) { pendingSeek=e.NewValue;seekDebounce.Stop();seekDebounce.Start(); } }
    private async void Repeat_Click(object sender, RoutedEventArgs e) => await EditQueue(() => { repeat = (repeat + 1) % 3; RepeatIcon.Glyph = repeat == 1 ? "\uE8ED" : "\uE8EE"; RepeatButton.Opacity = repeat == 0 ? .55 : 1; ToolTipService.SetToolTip(RepeatButton, new[] { "顺序播放", "单曲循环", "列表循环" }[repeat]); });
    private List<QueueItem>? originalQueue;
    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        await EditQueue(() => {
        shuffle = !shuffle; ShuffleButton.Opacity = shuffle ? 1 : .55;
        if (shuffle)
        {
            originalQueue = [.. queue];
            var tail = queue.Skip(index + 1).ToArray(); Random.Shared.Shuffle(tail); queue = queue.Take(index + 1).Concat(tail).ToList();
        }
        else if (originalQueue != null)
        {
            var played = queue.Take(index + 1).ToList(); var remaining = queue.Skip(index + 1).ToList();
            var ordered = new List<QueueItem>();
            foreach (var t in originalQueue) { int found = remaining.FindIndex(r => r.ItemId == t.ItemId); if (found >= 0) { ordered.Add(remaining[found]); remaining.RemoveAt(found); } }
            queue = played.Concat(ordered).Concat(remaining).ToList();
        }
        SaveQueue();
        });
    }
    private Task Import(IEnumerable<string> paths) => ImportWithPreview(paths);
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        try { var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); var folder = await picker.PickSingleFolderAsync(); if (folder != null) await Import([folder.Path]); }
        catch (Exception ex) { Show(ex.Message, true); }
    }
    private async void ImportFiles_Click(object sender, RoutedEventArgs e)
    {
        try { var picker = Picker(LibraryScanner.AudioExtensions.Concat(new[]{".cue"}).ToArray()); var files = await picker.PickMultipleFilesAsync(); if (files.Count > 0) await Import(files.Select(f => f.Path)); }
        catch (Exception ex) { Show(ex.Message, true); }
    }
    private FileOpenPicker Picker(params string[] extensions) { var picker = new FileOpenPicker(); foreach (var ext in extensions) picker.FileTypeFilter.Add(ext); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this)); return picker; }
    private void Root_DragOver(object sender, DragEventArgs e) { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; }
    private async void Root_Drop(object sender, DragEventArgs e) { try { if (e.DataView.Contains(StandardDataFormats.StorageItems)) await HandleDroppedItems((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path)); } catch (Exception ex) { Show(ex.Message, true); } }
    private async void Navigation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (Navigation.SelectedItem is not ListViewItem item || store == null) return;
        bool enteringPlayer = page != "now" && Equals(item.Tag, "now");
        page = item.Tag.ToString()!; UpdateNavigationGlass(); await Refresh();
        if (enteringPlayer) { clearLyrics = false; UpdateLyricsTiming(); LyricsCanvas.ResumeFollowing(true); }
    }
    private async void Search_Changed(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) { if (store != null) { if (page is "now" or "home") { Navigate("library"); } await Refresh(); } }
    private int refreshVersion;
    private async Task Refresh()
    {
        int version = ++refreshVersion;
        NowPage.Visibility = page == "now" ? Visibility.Visible : Visibility.Collapsed; LibraryPage.Visibility = page == "now" ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Visibility = page == "now" ? Visibility.Collapsed : Visibility.Visible;
        Layout();
        UpdateRenderActivity();
        if (page == "now") return;
        BackButton.Visibility = Visibility.Collapsed;
        activePlaylist = null; PlaylistActions.Visibility = Visibility.Collapsed;
        string query = SearchBox.Text.Trim();
        IEnumerable<Track> result = library;
        if (page == "favorites") result = result.Where(t => favorites.Contains(t.Id));
        if (page == "recent") result = recent.Select(id => library.FirstOrDefault(t => t.Id == id)).OfType<Track>();
        if (query.Length > 0) result = result.Where(t => $"{t.Title}\n{t.Artist}\n{t.Album}\n{t.Path}".Contains(query, StringComparison.CurrentCultureIgnoreCase));
        var sorted = await SortTracks(result);
        if(version!=refreshVersion||closed)return;
        visible = sorted;
        PageTitle.Text = page switch { "home" => "音乐库", "folders" => "文件夹", "albums" => "专辑", "artists" => "艺术家", "genres"=>"流派","smart"=>"智能歌单", "favorites" => "我喜欢的音乐", "recent" => "最近播放", "playlists" => "播放列表", _ => "全部歌曲" };
        PageSubtitle.Text = $"{visible.Count} 首歌曲"; LibraryCount.Text = $"{library.Count} 首 · 离线音乐库";
        bool grouped = page is "home" or "folders" or "albums" or "artists" or "playlists" or "genres" or "smart";
        TrackList.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible; Groups.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        TrackList.ItemsSource = visible;
        EmptyLibrary.Visibility = visible.Count == 0 || page == "playlists" && playlists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = library.Count == 0 ? "音乐，从这里开始" : "这里还没有音乐";
        if (!grouped) return;
        var groups = new List<MusicGroup>();
        IEnumerable<(string Name, List<Track> Tracks)> source = page == "smart" ? smartPlaylists.Values.Select(p=>(p.Name,p.Evaluate(visible,favorites,store.Usage(),DateTimeOffset.Now).ToList())) : page == "playlists"
            ? playlists.Select(p => (p.Key, ResolvePlaylist(p.Value).ToList()))
            : visible.GroupBy(t => page == "folders" ? Path.GetDirectoryName(t.Path)! : page == "artists" ? t.Artist : page=="genres"?t.Genre:t.Album + "\0" + t.AlbumArtist).Select(g => (g.Key.Split('\0')[0], g.ToList()));
        if (page == "home")
        {
            var history = recent.Select(id => library.FirstOrDefault(t => t.Id == id)).OfType<Track>().ToList();
            var liked = library.Where(t => favorites.Contains(t.Id)).ToList();
            source = new[] { ("最近播放", history), ("我喜欢的音乐", liked) }.Where(g => g.Item2.Count > 0).Concat(source);
            PageSubtitle.Text = "最近播放与本地专辑";
        }
        foreach (var group in source)
        {
            if (version != refreshVersion) return;
            int references = page == "playlists" ? playlists[group.Name].Count : group.Tracks.Count;
            groups.Add(new(group.Name, $"{references} 项" + (references > group.Tracks.Count ? $" · {references - group.Tracks.Count} 项路径待恢复" : ""), null, group.Tracks));
        }
        if (version == refreshVersion) { Groups.ItemsSource = groups; EmptyLibrary.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed; }
        await Task.CompletedTask;
    }
    private async void GroupCover_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) await LoadGroupCover(image);
    }
    private async void GroupCover_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender is Image { IsLoaded: true } image) await LoadGroupCover(image);
    }
    private async Task LoadGroupCover(Image image)
    {
        image.Source = null;
        if (image.DataContext is not MusicGroup group) return;
        if(group.Cover!=null){image.Source=group.Cover;return;}
        if(group.Tracks.Count==0)return;
        var source = await Cover(group.Tracks[0]); if (ReferenceEquals(image.DataContext, group)) image.Source = source;
    }
    private void Group_Click(object sender, ItemClickEventArgs e) { var group = (MusicGroup)e.ClickedItem; visible = group.Tracks.ToList(); Groups.Visibility = Visibility.Collapsed; TrackList.Visibility = Visibility.Visible; TrackList.ItemsSource = visible; PageTitle.Text = group.Name; PageSubtitle.Text = group.Detail; BackButton.Visibility = Visibility.Visible; activePlaylist = page == "playlists" ? group.Name : null; PlaylistActions.Visibility = activePlaylist == null ? Visibility.Collapsed : Visibility.Visible; }
    private async void Back_Click(object sender, RoutedEventArgs e) => await Refresh();
    private async void Track_Click(object sender, ItemClickEventArgs e) { if (busy) return; ReplaceQueue(visible, visible.IndexOf((Track)e.ClickedItem)); clock.Start(); await Start(queue[index].Track, true); }
    private async void PlayAll_Click(object sender, RoutedEventArgs e) { if (visible.Count == 0 || busy) return; ReplaceQueue(visible); clock.Start(); await Start(queue[0].Track, true); }
    private void Navigate(string tag) => Navigation.SelectedItem = Navigation.Items.Cast<ListViewItem>().First(item => Equals(item.Tag, tag));
    private void ShowNow_Click(object sender, RoutedEventArgs e) => Navigate("now");
    private void Track_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        FrameworkElement? element = e.OriginalSource as FrameworkElement; while (element != null && element.DataContext is not Track) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not Track track) return;
        var menu = new MenuFlyout();ApplyGlassToMenu(menu);
        var next = new MenuFlyoutItem { Text = "下一首播放", Icon = new SymbolIcon(Symbol.Next) }; next.Click += async (_, _) => await EditQueue(() => { int position = Math.Clamp(nextInsertion, index + 1, queue.Count); queue.Insert(position, new(Guid.NewGuid(), track)); nextInsertion = position + 1; }); menu.Items.Add(next);
        var favorite = new MenuFlyoutItem { Text = favorites.Contains(track.Id) ? "取消收藏" : "收藏", Icon = new SymbolIcon(Symbol.Favorite) }; favorite.Click += async (_, _) => { ToggleFavorite(track); await Refresh(); }; menu.Items.Add(favorite);
        var relocate = new MenuFlyoutItem { Text = "重新定位文件", Icon = new SymbolIcon(Symbol.Find) }; relocate.Click += async (_, _) => await LocateMissing(track); menu.Items.Add(relocate);
        var tags=new MenuFlyoutItem{Text="编辑文件标签",Icon=new SymbolIcon(Symbol.Edit),IsEnabled=track.CuePath==null};tags.Click+=async(_,_)=>await EditTags(track);menu.Items.Add(tags);
        menu.ShowAt(element); e.Handled = true;
    }
    private void ToggleFavorite(Track track) { if (!favorites.Add(track.Id)) favorites.Remove(track.Id); Save("favorites", favorites); UpdateFavorite(); }
    private void Favorite_Click(object sender, RoutedEventArgs e) { if (current != null) ToggleFavorite(current); }
    private void UpdateFavorite() { bool liked = current != null && favorites.Contains(current.Id); FavoriteIcon.Glyph = liked ? "\uEB52" : "\uEB51"; FavoriteIcon.Opacity = liked ? 1 : .6; }
    private void ClearLyrics_Click(object sender, RoutedEventArgs e) { clearLyrics = !clearLyrics; LyricsCanvas.SetClear(clearLyrics || !lyrics.Timed); }
    private Task<ContentDialogResult> Dialog(string title, object content, string? primary = null) => new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = EffectiveTheme, Title = title, Content = content, CloseButtonText = "关闭", PrimaryButtonText = primary ?? "", DefaultButton = ContentDialogButton.Close }.ShowAsync().AsTask();
    private ContentDialog? openSettings;
    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if(openSettings!=null)return;
        try
        {
        var panel = new StackPanel { Spacing = 18, MinWidth = 340 };
        var theme = new ComboBox { Header = "外观", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = (int)Root.RequestedTheme, HorizontalAlignment = HorizontalAlignment.Stretch }; panel.Children.Add(theme);
        var accentMode=new ComboBox{Header="主色调来源",ItemsSource=new[]{"跟随专辑封面","自选主色调"},SelectedIndex=store.Get("accent-mode")=="custom"?1:0,HorizontalAlignment=HorizontalAlignment.Stretch};
        var accentPicker=new ColorPicker{Color=SavedAccent(),IsAlphaEnabled=false,IsColorSliderVisible=false,IsColorChannelTextInputVisible=false,IsHexInputVisible=true};
        var accentPanel=new StackPanel{Spacing=12};accentPanel.Children.Add(accentMode);
        var paletteRow=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};
        foreach(var preset in new[]{("晚霞",Windows.UI.Color.FromArgb(255,203,136,102)),("海蓝",Windows.UI.Color.FromArgb(255,78,118,169)),("松绿",Windows.UI.Color.FromArgb(255,75,126,111)),("石灰",Windows.UI.Color.FromArgb(255,139,142,148)),("紫罗兰",Windows.UI.Color.FromArgb(255,153,106,180))})
        {var swatch=new Button{Width=40,Height=40,MinHeight=40,CornerRadius=new(20),Padding=new(0),Background=new SolidColorBrush(preset.Item2)};ToolTipService.SetToolTip(swatch,preset.Item1);AutomationProperties.SetName(swatch,preset.Item1+"主色调");swatch.Click+=(_,_)=>{accentMode.SelectedIndex=1;accentPicker.Color=preset.Item2;};paletteRow.Children.Add(swatch);}
        accentPanel.Children.Add(paletteRow);accentPanel.Children.Add(new Expander{Header="自定义颜色 / HEX",Content=accentPicker,HorizontalAlignment=HorizontalAlignment.Stretch});accentPanel.Children.Add(new TextBlock{Text="首页、侧栏和控制区域共用背景，播放页可跟随封面流光。",TextWrapping=TextWrapping.Wrap,FontSize=12});
        accentPicker.IsEnabled=accentMode.SelectedIndex==1;
        accentMode.SelectionChanged+=(_,_)=>{accentPicker.IsEnabled=accentMode.SelectedIndex==1;ApplyAccent(accentMode.SelectedIndex==1?"custom":"album",accentPicker.Color);};
        accentPicker.ColorChanged+=(_,_)=>ApplyAccent(accentMode.SelectedIndex==1?"custom":"album",accentPicker.Color);
        panel.Children.Add(new Expander{Header="主色调",Content=accentPanel,HorizontalAlignment=HorizontalAlignment.Stretch});
        var blur = new ToggleSwitch { Header = "非焦点歌词模糊", IsOn = LyricsCanvas.BlurEnabled }; panel.Children.Add(blur);
        var motion = new ToggleSwitch { Header = "减少动画", IsOn = LyricsCanvas.ReduceMotion }; panel.Children.Add(motion);
        var transparency = new ToggleSwitch { Header = "减少透明效果", IsOn = store.Get("transparency") == "reduced" }; panel.Children.Add(transparency);
        double backgroundBlurValue=PlaybackBackgroundBlur;
        var backgroundPanel=new StackPanel{Spacing=12};
        var backgroundBlurLabel=new TextBlock{Text=$"播放页背景模糊 · {backgroundBlurValue:0.#}"};backgroundPanel.Children.Add(backgroundBlurLabel);
        var backgroundBlur=new Slider{Minimum=0,Maximum=80,StepFrequency=.5,Value=backgroundBlurValue};AutomationProperties.SetName(backgroundBlur,"播放页背景模糊程度");backgroundPanel.Children.Add(backgroundBlur);
        backgroundBlur.ValueChanged+=(_,_)=>{backgroundBlurLabel.Text=$"播放页背景模糊 · {backgroundBlur.Value:0.#}";if(page=="now")flowingBackdrop?.SetBlur(store.Get("glass-economy")=="true"?Math.Min(8,backgroundBlur.Value):backgroundBlur.Value);};
        backgroundPanel.Children.Add(new TextBlock{Text="仅模糊流光背景，封面和歌词保持清晰。0 关闭；拖动实时预览，取消恢复。与玻璃模糊分开保存。",TextWrapping=TextWrapping.Wrap,FontSize=12});
        panel.Children.Add(new Expander{Header="播放背景",Content=backgroundPanel,HorizontalAlignment=HorizontalAlignment.Stretch});
        var glassPanel=new StackPanel{Spacing=12};var glassValues=glassRenderer?.Parameters??new GlassParameters();
        NumberBox GlassAdjustment(string title,double minimum,double maximum,double value,double step)
        {var row=new Grid{ColumnSpacing=12};row.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=new(82)});var slider=new Slider{Header=title,Minimum=minimum,Maximum=maximum,StepFrequency=step,Value=value};AutomationProperties.SetName(slider,title);var number=new NumberBox{Minimum=minimum,Maximum=maximum,SmallChange=step,Value=value,VerticalAlignment=VerticalAlignment.Bottom,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact};AutomationProperties.SetName(number,title+"精确值");slider.ValueChanged+=(_,args)=>{if(number.Value!=args.NewValue)number.Value=args.NewValue;};number.ValueChanged+=(_,args)=>{if(double.IsFinite(args.NewValue)&&slider.Value!=args.NewValue)slider.Value=args.NewValue;};row.Children.Add(slider);Grid.SetColumn(number,1);row.Children.Add(number);glassPanel.Children.Add(row);return number;}
        var glassRefraction=GlassAdjustment("折射强度",1,3.5,glassValues.Refraction,.05);
        var glassHighlight=GlassAdjustment("边缘高光",0,100,glassValues.Highlight,1);
        var controlBlurLabel=new TextBlock{Text=$"按钮背景模糊 · {glassValues.Blur:0.#}"};glassPanel.Children.Add(controlBlurLabel);
        var glassBlur=new Slider{Minimum=0,Maximum=32,StepFrequency=.5,Value=glassValues.Blur};AutomationProperties.SetName(glassBlur,"按钮背景模糊程度");glassPanel.Children.Add(glassBlur);
        var overlayBlurLabel=new TextBlock{Text=$"浮层背景模糊 · {glassValues.OverlayBlur:0.#}"};glassPanel.Children.Add(overlayBlurLabel);
        var glassOverlayBlur=new Slider{Minimum=0,Maximum=40,StepFrequency=.5,Value=glassValues.OverlayBlur};AutomationProperties.SetName(glassOverlayBlur,"浮层背景模糊程度");glassPanel.Children.Add(glassOverlayBlur);
        void PreviewGlassBlur(){controlBlurLabel.Text=$"按钮背景模糊 · {glassBlur.Value:0.#}";overlayBlurLabel.Text=$"浮层背景模糊 · {glassOverlayBlur.Value:0.#}";glassRenderer?.PreviewBlur(glassBlur.Value,glassOverlayBlur.Value);}
        glassBlur.ValueChanged+=(_,_)=>PreviewGlassBlur();glassOverlayBlur.ValueChanged+=(_,_)=>PreviewGlassBlur();
        glassPanel.Children.Add(new TextBlock{Text="拖动实时预览；0 为清晰背景，折射仍保留。保存后记住，取消恢复。",TextWrapping=TextWrapping.Wrap});
        var glassDispersion=GlassAdjustment("色散",0,5,glassValues.Dispersion,.05);
        var glassTint=GlassAdjustment("染色不透明度",0,.85,glassValues.TintOpacity,.01);
        void PreviewMaterial(){if(glassRenderer==null)return;glassRenderer.Parameters=new(glassRefraction.Value,glassHighlight.Value,glassBlur.Value,glassDispersion.Value,glassTint.Value,glassOverlayBlur.Value);glassRenderer.Configure(EffectiveDark,transparency.IsOn||IsHighContrast,motion.IsOn,store.Get("glass-economy")=="true");}
        foreach(var adjustment in new[]{glassRefraction,glassHighlight,glassDispersion,glassTint})adjustment.ValueChanged+=(_,_)=>{if(double.IsFinite(adjustment.Value))PreviewMaterial();};
        var glassEconomy=new ToggleSwitch{Header="玻璃性能模式",IsOn=store.Get("glass-economy")=="true"};glassPanel.Children.Add(glassEconomy);
        glassPanel.Children.Add(new TextBlock{Text=glassRenderer?.Diagnostic??"动态背景采样 · HLSL 折射与色散 · GPU 合成",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new Expander{Header="液态玻璃",Content=glassPanel,HorizontalAlignment=HorizontalAlignment.Stretch});
        var offset = new NumberBox { Header = "全局歌词校正（毫秒；正数延后）", Minimum = -5000, Maximum = 5000, SmallChange = 50, Value = globalLyricDelay * 1000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact }; panel.Children.Add(offset);
        var rescan = new Button { Content = "重新扫描音乐目录" }; rescan.Click += async (_, _) => await RescanDirectories(); panel.Children.Add(rescan);
        var cancel = new Button { Content = "取消扫描", IsEnabled = scan != null }; cancel.Click += (_, _) => scan?.Cancel(); panel.Children.Add(cancel);
        var pause=new Button{Content=scanControl?.IsPaused==true?"继续扫描":"暂停扫描",IsEnabled=scanControl!=null};pause.Click+=(_,_)=>{if(scanControl?.IsPaused==true){scanControl.Resume();pause.Content="暂停扫描";}else{scanControl?.Pause();pause.Content="继续扫描";}};panel.Children.Add(pause);
        var startup=new ToggleSwitch{Header="开机启动",IsOn=store.Get("startup")=="true"};panel.Children.Add(startup);
        var associations=new ToggleSwitch{Header="加入“打开方式”（默认应用由 Windows 设置选择）",IsOn=store.Get("associations")=="true"};panel.Children.Add(associations);
        var tray=new ToggleSwitch{Header="关闭窗口时留在托盘",IsOn=store.Get("tray")=="true"};panel.Children.Add(tray);
        var desktop=new Button{Content="显示 / 关闭桌面歌词"};desktop.Click+=(_,_)=>ToggleDesktopLyrics();panel.Children.Add(desktop);
        var diagnostics=new StackPanel{Spacing=12};
        var audioDetails=new Button{Content="音频信息"};audioDetails.Click+=(_,_)=>{afterSettingsClose=()=>AudioInfo_Click(this,new());openSettings?.Hide();};diagnostics.Children.Add(audioDetails);
        var audioDevices=new Button{Content="输出设备与音频设置"};audioDevices.Click+=(_,_)=>{afterSettingsClose=()=>Devices_Click(this,new());openSettings?.Hide();};diagnostics.Children.Add(audioDevices);
#if MUYU_DIAGNOSTICS
        diagnostics.Children.Add(new TextBlock{Text=glassRenderer?.Diagnostic??"未报告玻璃渲染错误",TextWrapping=TextWrapping.Wrap,FontSize=12});
#endif
        panel.Children.Add(new Expander{Header="音频",Content=diagnostics,HorizontalAlignment=HorizontalAlignment.Stretch});
        var buildShortcuts=ShortcutEditor(panel);var buildExtensions=LyricsExtensionEditor(panel);
        panel.Children.Add(new Expander{Header="关于听屿",HorizontalAlignment=HorizontalAlignment.Stretch,Content=new TextBlock{Text=$"听屿 {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}\n本地离线音乐播放器 · Windows x64\n数据目录：{dataFolder}\n许可与依赖：程序目录中的 THIRD_PARTY_NOTICES.md 和 licenses。\n便携版数据保存在相邻 data；安装版数据保存在当前用户目录。",TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true}});
        var validation=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(validation);var settingsDialog=new ContentDialog{XamlRoot=Root.XamlRoot,RequestedTheme=EffectiveTheme,Title="设置",Content=new ScrollViewer{Content=panel,MaxHeight=520},PrimaryButtonText="保存",CloseButtonText="取消"};ShortcutBinding[]? changedKeys=null;LyricsExtension[]? changedExtensions=null;GlassParameters? changedGlass=null;settingsDialog.PrimaryButtonClick+=(_,args)=>{try{changedKeys=buildShortcuts();changedExtensions=buildExtensions();if(!double.IsFinite(offset.Value))throw new FormatException("歌词校正须为有效数值");changedGlass=new GlassParameters(glassRefraction.Value,glassHighlight.Value,glassBlur.Value,glassDispersion.Value,glassTint.Value,glassOverlayBlur.Value);changedGlass.Validate();}catch(Exception ex){validation.Text=ex.Message;args.Cancel=true;}};
        openSettings=settingsDialog;var settingsResult=await settingsDialog.ShowAsync();openSettings=null;
        if(settingsResult!=ContentDialogResult.Primary){if(glassRenderer!=null)glassRenderer.Parameters=glassValues;ApplyVisualPreferences();return;}store.Set("accent-mode",accentMode.SelectedIndex==1?"custom":"album");store.Set("accent-color",$"#{accentPicker.Color.R:X2}{accentPicker.Color.G:X2}{accentPicker.Color.B:X2}");keyBindings=changedKeys!;Save("shortcuts",keyBindings);lyricsExtensions=changedExtensions!;Save("lyrics-extensions",lyricsExtensions);
        Root.RequestedTheme = (ElementTheme)theme.SelectedIndex; store.Set("theme", Root.RequestedTheme.ToString());
        LyricsCanvas.ReduceMotion = motion.IsOn; store.Set("motion", motion.IsOn ? "reduced" : "normal");
        LyricsCanvas.BlurEnabled = blur.IsOn && !transparency.IsOn && !IsHighContrast; store.Set("blur", blur.IsOn ? "true" : "false");
        store.Set("transparency", transparency.IsOn ? "reduced" : "normal");
        Save("glass-settings",changedGlass!);Save("playback-background-blur",backgroundBlur.Value);if(glassRenderer!=null)glassRenderer.Parameters=changedGlass!;store.Set("glass-economy",glassEconomy.IsOn?"true":"false");
        ApplyVisualPreferences();
        ApplyWindowsOptions(startup.IsOn,associations.IsOn,tray.IsOn);
        if (double.IsFinite(offset.Value)) { Save("lyrics-global-delay", offset.Value / 1000); globalLyricDelay = offset.Value / 1000; UpdateLyricsTiming(); }
        }
        catch (Exception ex) { Show("设置未完整保存：" + ex.Message, true); }
        finally{openSettings=null;var next=afterSettingsClose;afterSettingsClose=null;if(next!=null)DispatcherQueue.TryEnqueue(()=>next());}
    }
    private bool stacked;
    private double PlaybackBackgroundBlur
    {
        get { double value=Read<double?>("playback-background-blur")??16;return double.IsFinite(value)?Math.Clamp(value,0,80):16; }
    }
    private void ApplyVisualPreferences()
    {
        if(closed||visualsDisposed)return;
        bool dark = EffectiveDark;
        bool reduced = store.Get("transparency") == "reduced" || IsHighContrast;
        ApplyAccent();
        RefreshVisualPolish(dark,reduced);
        glassRenderer?.Configure(dark,reduced,store.Get("motion")=="reduced",store.Get("glass-economy")=="true");
        ApplyTitleBarTheme(dark);
        flowingBackdrop?.Configure(dark,!reduced&&store.Get("atmosphere")!="false",store.Get("motion")=="reduced",store.Get("glass-economy")=="true",PlaybackBackgroundBlur);
        UpdateRenderActivity();
    }
    private async Task UpdateAtmosphere(Track track, int token)
    {
        try
        {
            byte[]? bytes = await Task.Run(() => MetadataReader.Read(track.Path).Cover);
            if (bytes == null)
            {
                var path = new[] { "cover.jpg", "folder.jpg", "cover.png", "folder.png" }.Select(n => Path.Combine(Path.GetDirectoryName(track.Path)!, n)).FirstOrDefault(File.Exists);
                if (path != null && new FileInfo(path).Length < 16 * 1024 * 1024) bytes = await File.ReadAllBytesAsync(path);
            }
            if (bytes == null) { if (token == generation) {albumColors=AlbumColorEngine.Fallback;ApplyVisualPreferences();} return; }
            using var input = new MemoryStream(bytes).AsRandomAccessStream();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
            var pixels = (await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 32, ScaledHeight = 32 }, Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage)).DetachPixelData();
            if (token != generation || closed) return;
            albumColors=AlbumColorEngine.Analyze(pixels);ApplyVisualPreferences();
        }
        catch { if (token == generation) {albumColors=AlbumColorEngine.Fallback;ApplyVisualPreferences();} }
    }
    private void Layout()
    {
        if(closed||visualsDisposed)return;
        bool compact = Root.ActualWidth < 1180;
        bool immersive=page=="now";
        TransportMetadata.Visibility=Visibility.Visible;
        HeaderMiniButton.Visibility=immersive?Visibility.Collapsed:Visibility.Visible;
        double islandMargin=Root.ActualWidth<900?24:56;
        TransportSurface.Margin=TransportShadowHost.Margin=new Thickness(islandMargin,0,islandMargin,24);
        TransportProgress.Width=Math.Clamp((Root.ActualWidth-islandMargin*2-48)*.58,280,720);
        OutputBadge.Visibility=Root.ActualWidth<1180?Visibility.Collapsed:Visibility.Visible;
        double margin=Root.ActualWidth<900?24:immersive?48:40;
        ContentRegion.Margin=new Thickness(margin,immersive?8:24,margin,0);
        foreach (Button button in OutputButtons.Children.OfType<Button>()) { button.Width = Root.ActualWidth < 900 ? 36 : 40; button.Height = 40; }
        VolumeSlider.Visibility=Root.ActualWidth<1180?Visibility.Collapsed:Visibility.Visible;
        UpdateNavigationGlass();
        SidebarColumn.Width = new GridLength(immersive?0:compact?72:224);
        NavigationHost.Visibility=SidebarBackdrop.Visibility=BrandPanel.Visibility=immersive?Visibility.Collapsed:Visibility.Visible;
        ExitPlayerButton.Visibility=immersive?Visibility.Visible:Visibility.Collapsed;
        Brand.Visibility = ImportLabel.Visibility = LibraryCount.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SidebarImport.Padding = new Thickness(compact ? 4 : 10);
        foreach (ListViewItem item in Navigation.Items)
        {
            if (item.Content is StackPanel content && content.Children[1] is TextBlock label)
            { label.Visibility = compact ? Visibility.Collapsed : Visibility.Visible; ToolTipService.SetToolTip(item, label.Text); AutomationProperties.SetName(item, label.Text); }
        }
        if (Root.ActualWidth <= 884) stacked = true; else if (Root.ActualWidth >= 916) stacked = false;
        bool narrow = stacked;
        // Scrollable content extends under the glass. Only fixed artwork and
        // lyric focus/actions reserve the transport's occupied area.
        double transportHeight=Root.RowDefinitions[2].Height.Value;
        ArtworkPanel.Margin=new Thickness(0,0,0,narrow?0:transportHeight);
        LyricsCanvas.BottomInset=transportHeight+20;
        NoLyrics.Margin=new Thickness(0,0,0,transportHeight+20);
        NowColumns.ColumnDefinitions[0].Width = new GridLength(narrow ? 1 : .9, GridUnitType.Star);
        NowColumns.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1.1, GridUnitType.Star);
        NowColumns.RowDefinitions[0].Height = narrow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        NowColumns.RowDefinitions[1].Height = new GridLength(narrow ? 1 : 0, GridUnitType.Star);
        // A zero-height second row still consumes RowSpacing. In the side-by-side
        // player it clipped the artwork stack despite its bounds clearing the glass.
        NowColumns.RowSpacing = narrow ? 20 : 0;
        Grid.SetRow(LyricsPanel, narrow ? 1 : 0); Grid.SetColumn(LyricsPanel, narrow ? 0 : 1);
        ArtworkPanel.Orientation = narrow ? Orientation.Horizontal : Orientation.Vertical;
        ArtworkPanel.MaxWidth = narrow ? double.PositiveInfinity : 520;
        CoverFrame.Width = narrow ? (Root.ActualHeight < 700 ? 128 : 144) : Math.Max(140, Math.Min(520, (Root.ActualWidth - SidebarColumn.Width.Value - margin*2 - 48) * .45));
        // Measure the complete metadata, including a wrapped title and the quality badge,
        // before allocating the cover. ActualHeight may still describe the previous track.
        double metadataWidth=narrow?Math.Max(120,Root.ActualWidth-SidebarColumn.Width.Value-ContentRegion.Margin.Left-ContentRegion.Margin.Right-CoverFrame.Width-14):Math.Min(520,Math.Max(140,(Root.ActualWidth-margin*2-32)*.45));
        ArtworkMetadata.Width=metadataWidth;
        ArtworkMetadata.MinHeight=0;
        ArtworkMetadata.Measure(new Windows.Foundation.Size(metadataWidth,double.PositiveInfinity));
        double metadataHeight=ArtworkMetadata.DesiredSize.Height;
        ArtworkMetadata.MinHeight=metadataHeight;
        double artworkHeight=NowColumns.RowDefinitions[0].ActualHeight>0?NowColumns.RowDefinitions[0].ActualHeight:Math.Max(0,NowColumns.ActualHeight-NowColumns.RowSpacing);
        double safeCoverHeight=Math.Max(96,artworkHeight-transportHeight-metadataHeight-ArtworkPanel.Spacing-12);
        CoverFrame.Height = narrow ? CoverFrame.Width : Math.Min(CoverFrame.Width,safeCoverHeight);
        if (!narrow) CoverFrame.Width = CoverFrame.Height;
        ApplyVisualPreferences();
        DispatcherQueue.TryEnqueue(()=>{if(!closed)UpdateTitleBarRegions();});
    }
}
