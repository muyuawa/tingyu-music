using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private void LocateFiles_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();ApplyGlassToMenu(menu);
        var locate = new MenuFlyoutItem { Text = "查找缺失文件", Icon = new SymbolIcon(Symbol.Find) }; locate.Click += async (_, _) => await LocateMissing(null); menu.Items.Add(locate);
        var undo = new MenuFlyoutItem { Text = "撤销路径映射", Icon = new SymbolIcon(Symbol.Undo), IsEnabled = store.Get("path-mapping-undo") is string saved && saved != "null" };
        undo.Click += async (_, _) =>
        {
            if (busy || scan != null) { Show("请等待当前操作完成"); return; }
            busy = true;
            try { if (!SaveQueue()) return; var paths = await Task.Run(store.UndoRelocation); await RefreshMappedReferences(paths); Show("已撤销路径映射；原路径不可用的曲目仍显示为缺失"); }
            catch (Exception ex) { Show(ex.Message, true); }
            finally { busy = false; }
        }; menu.Items.Add(undo); menu.ShowAt((FrameworkElement)sender);
    }
    private async Task RefreshMappedReferences(IReadOnlyDictionary<string, string> paths)
    {
        queue = queue.Select(i => i with { Track = PlaybackSession.RemapTrack(i.Track, paths) }).ToList();
        if (originalQueue != null) originalQueue = originalQueue.Select(i => i with { Track = PlaybackSession.RemapTrack(i.Track, paths) }).ToList();
        await ReloadLibraryState();
        // Our atomic path remap advances the saved session exactly once. Do not adopt a competing writer's revision.
        sessionRevision = checked(sessionRevision + 1);
        if (current != null && paths.ContainsKey(current.Path) && (waitingToResume || audio.Status.State is 0 or 4))
        {
            long position = rememberedFrame;
            PresentTrack(PlaybackSession.RemapTrack(current, paths)); waitingToResume = true;
            rememberedFrame = position; resumeSeconds = (double)position / current!.SampleRate; UpdateStoredPosition();
        }
        await Refresh(); SaveQueue();
    }
    private async Task LocateMissing(Track? target)
    {
        if (scan != null) { Show("请先等待扫描完成"); return; }
        var all = await Task.Run(() => store.ReadAll(true));
        var tracks = await Task.Run(() => all.Where(t => (target == null ? t.InLibrary : string.Equals(t.Path, target.Path, StringComparison.OrdinalIgnoreCase)) && !File.Exists(t.Path)).ToArray());
        if (tracks.Length == 0) { Show("未发现缺失文件，当前没有需要替换的路径"); return; }
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"{tracks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()} 个来源需要检查" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var list = new ListView { Height = 230, SelectionMode = ListViewSelectionMode.Multiple, ItemTemplate = (DataTemplate)Root.Resources["WrappedTextItem"], ItemContainerStyle = (Style)Root.Resources["StretchListItem"] };
        var file = new Button { Content = "选择候选文件" };
        var folder = new Button { Content = "选择查找目录" };
        var retry = new Button { Content = "重试连接" };
        var commands = new StackPanel { Spacing = 8, Orientation = Orientation.Horizontal }; commands.Children.Add(file); commands.Children.Add(folder);
        var panel = new StackPanel { Spacing = 12 }; foreach (var item in new UIElement[] { summary, retry, commands, status, list }) panel.Children.Add(item);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "文件重新定位", Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "应用映射", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        bool searching = false, alive = true;
        async Task CheckConnection()
        {
            var states = await Task.Run(() => tracks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).Select(path => path + "\n" + Relocation.Availability(path)).ToArray());
            if (alive) summary.Text = string.Join("\n", states.Take(8)) + (states.Length > 8 ? $"\n另有 {states.Length - 8} 个来源" : "");
        }
        void Validate()
        {
            var selected = list.SelectedItems.OfType<RelocationCandidate>().ToArray();
            dialog.IsPrimaryButtonEnabled = !searching && selected.Length > 0 && selected.All(c => c.Verified) && selected.Select(c => c.OldPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == selected.Length && selected.Select(c => c.NewPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == selected.Length;
            dialog.PrimaryButtonText = selected.Length == 0 ? "应用映射" : $"应用 {selected.Length} 项映射";
        }
        async Task Search(string[]? files, string? root)
        {
            if (searching) return;
            searching = true; file.IsEnabled = folder.IsEnabled = false; dialog.IsPrimaryButtonEnabled = false; status.Text = "正在所选范围内核对内容指纹";
            try
            {
                var result = await Task.Run(() => {
                    var issues = new List<ScanIssue>();
                    var paths = files ?? LibraryScope.Enumerate(root!, true, cancellation.Token, issues).Where(path => LibraryScanner.AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
                    return (Rows: Relocation.Find(tracks, store.Fingerprints(tracks), paths, cancellation.Token), Issues: issues.Count);
                }, cancellation.Token);
                if (!alive) return;
                list.ItemsSource = result.Rows;
                foreach (var group in result.Rows.Where(r => r.Verified).GroupBy(r => r.OldPath, StringComparer.OrdinalIgnoreCase))
                    if (group.Count() == 1) list.SelectedItems.Add(group.Single());
                status.Text = $"已找到 {result.Rows.Count(r => r.Verified)} 个可靠候选。多候选需手动保留一个。" + (result.Issues > 0 ? $"\n{result.Issues} 个路径未读取或已跳过" : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (alive) status.Text = ex.Message; }
            finally { searching = false; if (alive) { file.IsEnabled = folder.IsEnabled = true; Validate(); } }
        }
        list.SelectionChanged += (_, _) => Validate();
        list.ContainerContentChanging += (_, args) => { if (args.Item is RelocationCandidate candidate) args.ItemContainer.IsEnabled = candidate.Verified; };
        retry.Click += async (_, _) => await CheckConnection();
        file.Click += async (_, _) => { try { var files = await Picker(LibraryScanner.AudioExtensions).PickMultipleFilesAsync(); if (files.Count > 0) await Search(files.Select(f => f.Path).ToArray(), null); } catch (Exception ex) { status.Text = ex.Message; } };
        folder.Click += async (_, _) => { try { string? root = await PickFolder(); if (root != null) await Search(null, root); } catch (Exception ex) { status.Text = ex.Message; } };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            if (busy || scan != null) { status.Text = "请等待当前操作完成"; return; }
            var deferral = args.GetDeferral(); busy = true; dialog.IsEnabled = false;
            try
            {
                var selected = list.SelectedItems.OfType<RelocationCandidate>().ToArray(); if (!SaveQueue()) return;
                var plan = await Task.Run(() => {
                    var prepared = Relocation.Prepare(store.ReadAll(true), selected, cancellation.Token);
                    store.ApplyRelocation(prepared); return prepared;
                }, cancellation.Token);
                await RefreshMappedReferences(plan.Mappings.ToDictionary(m => m.OldPath, m => m.NewPath, StringComparer.OrdinalIgnoreCase));
                Show("文件已找到，点击播放后继续；没有自动播放。可在重新定位菜单撤销映射"); args.Cancel = false;
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; dialog.IsEnabled = true; deferral.Complete(); }
        };
        try { await CheckConnection(); await dialog.ShowAsync(); }
        catch (Exception ex) { Show(ex.Message, true); }
        finally { alive = false; cancellation.Cancel(); }
    }
}
