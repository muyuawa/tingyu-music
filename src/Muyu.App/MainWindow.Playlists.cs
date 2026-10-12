using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private Dictionary<string, Track> playlistTracks = [];
    private string? activePlaylist;
    private IEnumerable<Track> ResolvePlaylist(IEnumerable<string> ids)
    {
        var indexed = knownTracks;
        return ids.Select(id => indexed.GetValueOrDefault(id) ?? playlistTracks.GetValueOrDefault(id)).OfType<Track>();
    }
    private void CommitPlaylists(Dictionary<string, List<string>> next, IEnumerable<Track>? snapshots = null)
    {
        var savedTracks = new Dictionary<string, Track>(playlistTracks);
        if (snapshots != null) foreach (var track in snapshots) savedTracks[track.Id] = track;
        store.SetMany(new Dictionary<string, string> {
            ["playlists"] = JsonSerializer.Serialize(next),
            ["playlist-track-snapshots"] = JsonSerializer.Serialize(savedTracks)
        });
        playlists = next; playlistTracks = savedTracks;
    }
    private async Task SaveQueuePlaylist()
    {
        if (!await RefreshCurrent()) return;
        var snapshot = queue.ToArray(); int currentIndex = index;
        var name = new TextBox { Header = "播放列表名称", MaxLength = 120 };
        var includeHistory = new CheckBox { Content = "包含已播放项目", IsChecked = false };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 14 };
        foreach (var item in new UIElement[] { name, includeHistory, summary, error }) panel.Children.Add(item);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "保存队列为播放列表", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        Track[] Selected() => PlaylistSelection.FromQueue(snapshot, currentIndex, includeHistory.IsChecked == true);
        void Preview()
        {
            var selected = Selected();
            summary.Text = $"{selected.Length} 项 · 重复项 {PlaylistSelection.Duplicates(selected)} 个";
            try { PlaylistSelection.ValidateName(name.Text, playlists.Keys); dialog.IsPrimaryButtonEnabled = selected.Length > 0; error.Text = ""; }
            catch (ArgumentException ex) { dialog.IsPrimaryButtonEnabled = false; error.Text = name.Text.Length == 0 ? "" : ex.Message; }
        }
        name.TextChanged += (_, _) => Preview(); includeHistory.Checked += (_, _) => Preview(); includeHistory.Unchecked += (_, _) => Preview(); Preview();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                string title = PlaylistSelection.ValidateName(name.Text, playlists.Keys);
                var selected = Selected();
                if (selected.Length == 0) throw new InvalidOperationException("没有可保存的曲目");
                var next = new Dictionary<string, List<string>>(playlists) { [title] = selected.Select(t => t.Id).ToList() };
                CommitPlaylists(next, selected); Show($"已保存「{title}」· {selected.Length} 项");
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        try { await dialog.ShowAsync(); if (page == "playlists") await Refresh(); }
        catch (Exception ex) { Show(ex.Message, true); }
    }
    private void PlaylistActions_Click(object sender, RoutedEventArgs e)
    {
        if (activePlaylist != null) ShowPlaylistMenu(activePlaylist, (FrameworkElement)sender);
    }
    private void Group_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (page != "playlists") return;
        var element = e.OriginalSource as FrameworkElement;
        while (element != null && element.DataContext is not MusicGroup) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        if (element?.DataContext is not MusicGroup group) return;
        ShowPlaylistMenu(group.Name, element); e.Handled = true;
    }
    private void ShowPlaylistMenu(string name, FrameworkElement anchor)
    {
        var menu = new MenuFlyout();ApplyGlassToMenu(menu);
        var export = new MenuFlyoutItem { Text = "导出 M3U8", Icon = new SymbolIcon(Symbol.Save) };
        export.Click += async (_, _) => await ExportPlaylist(name); menu.Items.Add(export);
        var rename = new MenuFlyoutItem { Text = "重命名", Icon = new SymbolIcon(Symbol.Edit) };
        rename.Click += async (_, _) => await RenamePlaylist(name); menu.Items.Add(rename);
        var remove = new MenuFlyoutItem { Text = "删除播放列表", Icon = new SymbolIcon(Symbol.Delete) };
        remove.Click += async (_, _) =>
        {
            try
            {
                if (await Dialog("删除「" + name + "」？", new TextBlock { Text = "只删除此播放列表。原音乐、收藏与当前队列保持不变。", TextWrapping = TextWrapping.Wrap }, "删除播放列表") != ContentDialogResult.Primary) return;
                var next = new Dictionary<string, List<string>>(playlists); next.Remove(name); CommitPlaylists(next); await Refresh();
            }
            catch (Exception ex) { Show(ex.Message, true); }
        }; menu.Items.Add(remove); menu.ShowAt(anchor);
    }
    private async Task RenamePlaylist(string original)
    {
        var name = new TextBox { Header = "播放列表名称", Text = original, MaxLength = 120 };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(name); panel.Children.Add(error);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "重命名播放列表", Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                string title = PlaylistSelection.ValidateName(name.Text, playlists.Keys, original);
                if (!playlists.TryGetValue(original, out var items)) throw new InvalidOperationException("播放列表已变化");
                var next = new Dictionary<string, List<string>>(playlists); next.Remove(original); next.Add(title, items); CommitPlaylists(next);
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        try { if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Refresh(); }
        catch (Exception ex) { Show(ex.Message, true); }
    }
    private async Task ExportPlaylist(string name)
    {
        if (!playlists.TryGetValue(name, out var ids)) return;
        var snapshot = ResolvePlaylist(ids).ToArray(); int unresolved = ids.Count - snapshot.Length;
        var cue = new ComboBox { Header = "CUE 兼容策略", ItemsSource = new[] { "请选择", "只导出普通文件曲目", "每个整盘音源仅保留一次" }, SelectedIndex = snapshot.Any(t => t.CuePath != null) ? 0 : 1, HorizontalAlignment = HorizontalAlignment.Stretch };
        var paths = new ComboBox { Header = "路径", ItemsSource = new[] { "相对路径", "绝对路径" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var skip = new CheckBox { Content = "跳过缺失文件", IsChecked = false };
        var missingRefs = new CheckBox { Content = $"跳过 {unresolved} 个没有可恢复路径的旧引用", IsChecked = false, Visibility = unresolved > 0 ? Visibility.Visible : Visibility.Collapsed };
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var rows = new ListView { Height = 200, SelectionMode = ListViewSelectionMode.None, ItemTemplate = (DataTemplate)Root.Resources["WrappedTextItem"], ItemContainerStyle = (Style)Root.Resources["StretchListItem"] };
        var panel = new StackPanel { Spacing = 12 };
        foreach (var item in new UIElement[] { cue, paths, skip, missingRefs, summary, rows }) panel.Children.Add(item);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "导出 M3U8 · " + name, Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "导出", CloseButtonText = "取消" };
        PlaylistExportPlan? plan = null, completed = null; string? completedPath = null; bool alive = true; int previewRevision = 0;
        async Task Preview()
        {
            int request = ++previewRevision; dialog.IsPrimaryButtonEnabled = false; summary.Text = "正在检查文件";
            var cuePolicy = (CueExportPolicy)cue.SelectedIndex; var pathStyle = (PlaylistPathStyle)paths.SelectedIndex; bool skipMissing = skip.IsChecked == true;
            try
            {
                var result = await Task.Run(() => PlaylistExport.Preview(snapshot, cuePolicy, pathStyle, skipMissing));
                if (!alive || request != previewRevision) return;
                plan = result;
                summary.Text = $"原始 {ids.Count} 项 · 写入 {plan.Written} 项 · 跳过 {plan.Skipped + unresolved} 项 · 警告 {plan.Warnings + unresolved} 项" +
                    (plan.Error == null ? "" : "\n" + plan.Error) + (pathStyle == PlaylistPathStyle.Relative ? "\n相对路径基准：保存位置待选择；跨卷保留绝对路径" : "");
                rows.ItemsSource = plan.Rows.Select(row => $"{row.Title}\n{row.Result}\n{row.OutputPath ?? row.SourcePath}").ToArray();
                dialog.IsPrimaryButtonEnabled = plan.CanWrite && (unresolved == 0 || missingRefs.IsChecked == true);
            }
            catch (Exception ex) { if (alive && request == previewRevision) summary.Text = ex.Message; }
        }
        cue.SelectionChanged += async (_, _) => await Preview(); paths.SelectionChanged += async (_, _) => await Preview();
        skip.Checked += async (_, _) => await Preview(); skip.Unchecked += async (_, _) => await Preview();
        missingRefs.Checked += async (_, _) => await Preview(); missingRefs.Unchecked += async (_, _) => await Preview();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true; dialog.IsEnabled = false;
            try
            {
                if (plan?.CanWrite != true) return;
                var picker = new FileSavePicker { SuggestedFileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) };
                picker.FileTypeChoices.Add("UTF-8 播放列表", new List<string> { ".m3u8" });
                InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                var selected = await picker.PickSaveFileAsync(); if (selected == null) return;
                string destination = selected.Path;
                var frozen = PlaylistExport.AtDestination(plan, (PlaylistPathStyle)paths.SelectedIndex, destination);
                await Task.Run(() => PlaylistExport.Write(destination, frozen));
                Show($"已导出：{destination} · 写入 {frozen.Written} 项，跳过 {frozen.Skipped + unresolved} 项，警告 {frozen.Warnings + unresolved} 项");
                completed = frozen; completedPath = destination;
                args.Cancel = false;
            }
            catch (Exception ex) { summary.Text = ex.Message; }
            finally { dialog.IsEnabled = true; deferral.Complete(); }
        };
        try
        {
            await Preview(); await dialog.ShowAsync();
            if (completed != null)
            {
                var report = new StackPanel { Spacing = 12 };
                report.Children.Add(new TextBlock { Text = $"{completedPath}\n写入 {completed.Written} 项 · 跳过 {completed.Skipped + unresolved} 项 · 警告 {completed.Warnings + unresolved} 项", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                report.Children.Add(new ListView { Height = 280, SelectionMode = ListViewSelectionMode.None, ItemTemplate = (DataTemplate)Root.Resources["WrappedTextItem"], ItemContainerStyle = (Style)Root.Resources["StretchListItem"], ItemsSource = completed.Rows.Select(row => $"{row.Title}\n{row.Result}\n{row.OutputPath ?? row.SourcePath}").ToArray() });
                await Dialog("导出结果", report);
            }
        }
        catch (Exception ex) { Show(ex.Message, true); }
        finally { alive = false; ++previewRevision; }
    }
}
