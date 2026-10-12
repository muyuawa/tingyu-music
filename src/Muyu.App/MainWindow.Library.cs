using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private Dictionary<string, Track> knownTracks = [];
    private readonly List<FileSystemWatcher> libraryWatchers = [];
    private readonly Dictionary<string, DateTime> dirtyRoots = [];
    private readonly object watchGate = new();
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime lastReconcile = DateTime.UtcNow;
    private bool monitoringTick;
    private ScanControl? scanControl;
    private sealed record ImportReport(string Path, int Checked, int Added, int Existing, int Cue, bool Cancelled, ScanIssue[] Issues);
    private async Task ReloadLibraryState()
    {
        var all = await Task.Run(() => store.ReadAll(true));
        if (closed) return;
        knownTracks = all.ToDictionary(t => t.Id); library = all.Where(t => t.InLibrary).ToList();
        playlistTracks = Read<Dictionary<string, Track>>("playlist-track-snapshots") ?? playlistTracks;
    }
    private async Task ImportWithPreview(IEnumerable<string> paths)
    {
        if (scan != null) { Show("正在扫描，可在设置中取消"); return; }
        var files = new List<string>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Directory.Exists(path) || !LibraryScanner.Supported(path))
            {
                var root = await ConfirmRoot(path); if (root != null) await ScanLibrary([root.Path], root);
            }
            else files.Add(path);
        }
        if (files.Count > 0) await ScanLibrary(files, null);
        ConfigureLibraryMonitoring();
    }
    private async Task<LibraryRoot?> ConfirmRoot(string initialPath)
    {
        string selectedPath = initialPath;
        var old = store.Roots().FirstOrDefault(r => string.Equals(r.Path, initialPath, StringComparison.OrdinalIgnoreCase));
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var recursive = new CheckBox { Content = "包含子文件夹", IsChecked = old?.Recursive ?? true };
        var watch = new CheckBox { Content = "持续监控", IsChecked = old?.Watch ?? true };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var choose = new Button { Content = "重新选择" };
        var panel = new StackPanel { Spacing = 14 }; foreach (var item in new UIElement[] { path, recursive, watch, status, choose }) panel.Children.Add(item);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "添加目录 · 范围预览", Content = panel, PrimaryButtonText = "开始扫描", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        int revision = 0; bool alive = true;
        async Task Check()
        {
            int request = ++revision; string candidate = selectedPath; path.Text = candidate; status.Text = "正在检查目录权限"; dialog.IsPrimaryButtonEnabled = false;
            try
            {
                await Task.Run(() => LibraryScope.ValidateDirectory(candidate));
                if (!alive || request != revision) return;
                var overlaps = store.Roots().Where(r => LibraryScope.Contains(r.Path, candidate) || LibraryScope.Contains(candidate, r.Path)).ToArray();
                status.Text = "文件数尚未统计" + (overlaps.Length == 0 ? "" : "\n与以下目录重叠，文件不会重复入库：\n" + string.Join("\n", overlaps.Select(r => r.Path)));
                dialog.IsPrimaryButtonEnabled = true;
            }
            catch (Exception ex) { if (alive && request == revision) status.Text = "目录离线或无法读取：" + ex.Message; }
        }
        choose.Click += async (_, _) =>
        {
            try { var folder = await PickFolder(); if (folder != null) { selectedPath = folder; await Check(); } }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        LibraryRoot? result = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true; dialog.IsEnabled = false;
            try
            {
                string candidate = selectedPath; bool subfolders = recursive.IsChecked == true, monitor = watch.IsChecked == true;
                result = await Task.Run(() => store.RegisterRoot(candidate, subfolders, monitor)); args.Cancel = false;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { dialog.IsEnabled = true; deferral.Complete(); }
        };
        try { await Check(); await dialog.ShowAsync(); return result; }
        finally { alive = false; ++revision; }
    }
    private async Task<string?> PickFolder()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async Task ScanLibrary(IEnumerable<string> inputPaths, LibraryRoot? root, bool background = false)
    {
        if (scan != null || closed) return;
        var input = inputPaths.ToArray();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); scan = cancellation; ScanProgress.Visibility = Visibility.Visible;
        scanControl=new ScanControl();
        var issues = new List<ScanIssue>(); int added = 0, existing = 0, cue = 0, count = 0; bool cancelled = false;
        try
        {
            var previous = knownTracks.Keys.ToHashSet();
            var scanKnown=knownTracks.Values.ToArray();
            var fingerprints = store.Fingerprints().ToDictionary(i => i.Key, i => i.Value, StringComparer.OrdinalIgnoreCase);
            var result = await Task.Run(() => {
                if (root != null && LocalFileIdentity.DirectoryIdentity(root.Path) != root.Identity) throw new IOException("目录身份已变化，未扫描替换目录");
                return LibraryScanner.Scan(input, cancellation.Token, new Progress<int>(n => DispatcherQueue.TryEnqueue(() => { if (!closed && ReferenceEquals(scan, cancellation)) PageSubtitle.Text = $"已检查 {n} 个文件 · 新增 {added} 项 · 警告 {issues.Count} 项"; })), null,root?.Recursive ?? true,scanControl,verifiedCommit:(batch,scanEvidence) => {
                    var ready = new List<Track>(); var evidence = new Dictionary<string, SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
                    foreach (var source in batch.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        try { var expected=scanEvidence[source.Key];var actual=LocalFileIdentity.Capture(source.Key,cancellation.Token,expected);if(actual!=expected)throw new IOException("音源在扫描后发生变化，未提交旧格式信息");evidence[source.Key]=expected;ready.AddRange(source); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(source.Key, "fingerprint", ex.Message)); }
                    }
                    var committed = store.AddBatch(ready, root?.Id, evidence);
                    existing += ready.Count - committed.Count;
                    foreach (var item in committed) { if (previous.Add(item.Id)) added++; else existing++; if (item.CuePath != null) cue++; }
                    foreach (var pair in evidence) fingerprints[pair.Key] = pair.Value;
                },cachedFingerprints:fingerprints,knownTracks:scanKnown);
            }, cancellation.Token);
            count = result.CheckedFiles; issues.AddRange(result.Issues ?? []);
        }
        catch (ScanInterruptedException ex) { cancelled = true; count = ex.PartialResult.CheckedFiles; issues.AddRange(ex.PartialResult.Issues ?? []); }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex) { issues.Add(new(root?.Path ?? string.Join(", ", input), "scan", ex.Message)); }
        finally
        {
            scan = null; cancellation.Dispose();
            scanControl?.Dispose();scanControl=null;
            if (!closed)
            {
                ScanProgress.Visibility = Visibility.Collapsed;
                var report = new ImportReport(root?.Path ?? string.Join(", ", input), count, added, existing, cue, cancelled, issues.ToArray());
                try
                {
                    Save("last-scan-report", report); await ReloadLibraryState(); await Refresh();
                    if (!background || added > 0 || issues.Count > 0)
                        Show($"{(cancelled ? "扫描已取消，已提交记录保留" : issues.Count == 0 ? "扫描完成" : "扫描部分完成")} · 新增 {added} 项 · 已存在 {existing} 项 · CUE {cue} 项 · 警告 {issues.Count} 项", issues.Count > 0);
                }
                catch (Exception ex) { Show("扫描结果未完整保存：" + ex.Message, true); }
            }
        }
    }
    private void StopLibraryMonitoring()
    {
        monitorTimer.Stop(); monitorTimer.Tick -= MonitorLibrary;
        foreach (var watcher in libraryWatchers) watcher.Dispose(); libraryWatchers.Clear();
    }
    private void ConfigureLibraryMonitoring()
    {
        StopLibraryMonitoring();
        if (closed) return;
        foreach (var root in store.Roots().Where(r => r.Watch))
        {
            try
            {
                if (!Directory.Exists(root.Path)) continue;
                var watcher = new FileSystemWatcher(root.Path) { IncludeSubdirectories = root.Recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                void Dirty() { lock (watchGate) dirtyRoots[root.Id] = DateTime.UtcNow; }
                bool Relevant(string path) => LibraryScanner.Supported(path) || new[] { ".lrc", ".jpg", ".png", "" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || Directory.Exists(path);
                watcher.Changed += (_, e) => { if (Relevant(e.FullPath)) Dirty(); }; watcher.Created += (_, e) => { if (Relevant(e.FullPath)) Dirty(); }; watcher.Deleted += (_, e) => { if (Relevant(e.FullPath)) Dirty(); }; watcher.Renamed += (_, e) => { if (Relevant(e.FullPath) || Relevant(e.OldFullPath)) Dirty(); }; watcher.Error += (_, _) => Dirty();
                watcher.EnableRaisingEvents = true; libraryWatchers.Add(watcher);
            }
            catch (Exception ex) { Show("目录监控未启动：" + ex.Message, true); }
        }
        monitorTimer.Tick += MonitorLibrary; monitorTimer.Start();
    }
    private async void MonitorLibrary(object? sender, object e)
    {
        if (closed || monitoringTick || scan != null || busy) return;
        monitoringTick = true;
        try
        {
            var roots = store.Roots().Where(r => r.Watch).ToArray();
            if (DateTime.UtcNow - lastReconcile > TimeSpan.FromMinutes(5))
            {
                lock (watchGate) foreach (var root in roots) dirtyRoots.TryAdd(root.Id, DateTime.UtcNow - TimeSpan.FromSeconds(3));
                lastReconcile = DateTime.UtcNow;
            }
            LibraryRoot? pending;
            lock (watchGate)
            {
                pending = roots.FirstOrDefault(r => dirtyRoots.TryGetValue(r.Id, out var when) && DateTime.UtcNow - when >= TimeSpan.FromSeconds(2));
                if (pending != null) dirtyRoots.Remove(pending.Id);
            }
            if (pending != null) await ScanLibrary([pending.Path], pending, true);
        }
        catch (Exception ex) { Show("目录对账失败：" + ex.Message, true); }
        finally { monitoringTick = false; }
    }
    private async void ManageFolders_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { Height = 220, SelectionMode = ListViewSelectionMode.Single, ItemTemplate = (DataTemplate)Root.Resources["WrappedTextItem"], ItemContainerStyle = (Style)Root.Resources["StretchListItem"] };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var roots = store.Roots(); list.ItemsSource = roots.Select(r => r.Path + $"\n{(r.Recursive ? "包含子文件夹" : "仅本层")} · {(r.Watch ? "持续监控" : "手动扫描")}").ToArray();
        var report = new Button { Content = "上次扫描报告" };
        var rescan = new Button { Content = "重新扫描" };
        var remove = new Button { Content = "移除目录" };
        var edit = new Button { Content = "调整范围与监控" };
        var panel = new StackPanel { Spacing = 12 }; foreach (var item in new UIElement[] { list, status, rescan, edit, remove, report }) panel.Children.Add(item);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "音乐目录", Content = panel, CloseButtonText = "关闭" };
        LibraryRoot? selected = null; string action = "";
        remove.Click += (_, _) => { if (list.SelectedIndex >= 0) { selected = roots[list.SelectedIndex]; action = "remove"; dialog.Hide(); } };
        rescan.Click += (_, _) => { if (list.SelectedIndex >= 0) { selected = roots[list.SelectedIndex]; action = "scan"; dialog.Hide(); } };
        edit.Click += (_, _) => { if (list.SelectedIndex >= 0) { selected = roots[list.SelectedIndex]; action = "edit"; dialog.Hide(); } };
        report.Click += (_, _) => { action = "report"; dialog.Hide(); };
        list.SelectionChanged += (_, _) => { if (list.SelectedIndex >= 0) status.Text = $"关联 {store.RootTrackCount(roots[list.SelectedIndex].Id)} 个曲目引用"; };
        try
        {
            await dialog.ShowAsync();
            if (action == "report") { await ShowScanReport(); return; }
            if (selected == null) return;
            if (scan != null) { Show("请先等待或取消扫描，再调整目录"); return; }
            if (action == "edit") { var updated = await ConfirmRoot(selected.Path); if (updated != null) await ScanLibrary([updated.Path], updated); ConfigureLibraryMonitoring(); }
            if (action == "scan") { await ScanLibrary([selected.Path], selected); ConfigureLibraryMonitoring(); }
            if (action == "remove")
            {
                if (scan != null) { Show("请先取消扫描，再移除目录"); return; }
                if (await Dialog("移除此目录的监控和库索引？", new TextBlock { Text = selected.Path + $"\n关联 {store.RootTrackCount(selected.Id)} 个曲目引用。原音乐不删除，其他目录覆盖的曲目保留。", TextWrapping = TextWrapping.Wrap }, "移除目录") != ContentDialogResult.Primary) return;
                store.RemoveRoot(selected.Id); ConfigureLibraryMonitoring(); await ReloadLibraryState(); await Refresh(); Show("目录已移除，原音乐与播放队列未删除");
            }
        }
        catch (Exception ex) { Show(ex.Message, true); }
    }
    private async Task RescanDirectories()
    {
        var roots = store.Roots();
        if (roots.Count == 0) { Show("请从添加目录入口确认旧目录的扫描范围"); return; }
        foreach (var root in roots) await ScanLibrary([root.Path], root);
        ConfigureLibraryMonitoring();
    }
    private async Task ShowScanReport()
    {
        var report = Read<ImportReport>("last-scan-report"); if (report == null) { Show("还没有扫描报告"); return; }
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = $"{report.Path}\n已检查 {report.Checked} 个文件 · 新增 {report.Added} · 已存在 {report.Existing} · CUE {report.Cue}\n遗漏目录 {report.Issues.Count(i => i.Kind == "directory")} · 警告 {report.Issues.Length}" , TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ListView { Height = 220, ItemTemplate = (DataTemplate)Root.Resources["WrappedTextItem"], ItemContainerStyle = (Style)Root.Resources["StretchListItem"], ItemsSource = report.Issues.Select(i => i.Path + "\n" + i.Message).ToArray() });
        if (await Dialog("扫描报告", panel, report.Issues.Length > 0 ? "重试失败项" : null) == ContentDialogResult.Primary)
        {
            var roots = store.Roots().Where(r => report.Issues.Any(i => LibraryScope.Contains(r.Path, i.Path, r.Recursive))).ToArray();
            foreach (var root in roots) await ScanLibrary(report.Issues.Where(i => LibraryScope.Contains(root.Path, i.Path, root.Recursive) && i.Kind != "link").Select(i => i.Path), root);
            var files = report.Issues.Where(i => i.Kind != "link" && !roots.Any(r => LibraryScope.Contains(r.Path, i.Path, r.Recursive))).Select(i => i.Path).ToArray();
            if (files.Length > 0) await ScanLibrary(files, null);
        }
    }
    private async Task HandleDroppedItems(IEnumerable<string> paths)
    {
        var items = paths.ToArray(); var directories = items.Where(Directory.Exists).ToArray();
        if (directories.Length > 0) await ImportWithPreview(directories);
        var files = items.Except(directories).Where(LibraryScanner.Supported).ToArray(); if (files.Length == 0) return;
        var action = new ComboBox { Header = "添加音频", ItemsSource = new[] { "加入音乐库", "加入队列", "立即播放" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (await Dialog($"{files.Length} 个音频 / CUE 文件", action, "确认") != ContentDialogResult.Primary) return;
        if (action.SelectedIndex == 0) { await ScanLibrary(files, null); return; }
        var result = await Task.Run(() => LibraryScanner.Scan(files, lifetime.Token));
        if (result.Tracks.Count == 0) { Show(result.Errors.FirstOrDefault() ?? "没有可播放的曲目", true); return; }
        if (busy) return;
        var direct=result.Tracks.Select(t=>t with{Id=Track.SourceIdentity(result.Fingerprints![t.Path],t.StartFrame,t.EndFrame),InLibrary=false}).ToArray();store.RememberSources(direct,result.Fingerprints!);
        if (action.SelectedIndex == 2) { ReplaceQueue(direct); clock.Start(); await Start(queue[0].Track, true); }
        else await EditQueue(() => queue.AddRange(direct.Select(t => new QueueItem(Guid.NewGuid(), t))));
        if (result.Errors.Count > 0) Show($"已处理可用曲目；{result.Errors.Count} 个文件有错误", true);
    }
}
