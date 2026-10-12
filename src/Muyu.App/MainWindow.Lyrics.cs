using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private LyricsBinding? activeLyricsBinding;
    private LyricCorrectionDraft? lyricDraft;
    private double globalLyricDelay;
    private string? presentedLyricsRevision;
    private Action? correctionStatusRefresh;
    private sealed record LyricsChoice(string Name, string Path, bool Embedded = false, LyricsBinding? Saved = null)
    {
        public override string ToString() => Name;
    }
    private LyricsBindingHistory? ReadLyricsHistory(Track track) => Read<LyricsBindingHistory>("lyrics-binding:" + track.Id);
    private void SaveLyricsBinding(Track target, LyricsBinding binding)
    {
        var history = ReadLyricsHistory(target);
        Save("lyrics-binding:" + target.Id, history == null ? new(binding, Array.Empty<LyricsBinding>()) : history.Replace(binding));
        if (current?.Id == target.Id) { activeLyricsBinding = binding; ApplyBoundLyrics(); }
    }
    private void ApplyBoundLyrics()
    {
        var next = activeLyricsBinding?.Snapshot ?? Lyrics.Empty;
        bool changed = presentedLyricsRevision != activeLyricsBinding?.Revision;
        lyrics = next;
        if (changed || activeLyricsBinding == null) LyricsCanvas.Load(lyrics);
        presentedLyricsRevision = activeLyricsBinding?.Revision;
        LyricsCanvas.SetClear(clearLyrics || !lyrics.Timed);
        NoLyrics.Visibility = lyrics.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLyricsTiming();
    }
    private void UpdateLyricsTiming()
    {
        if (current == null) return;
        lyricBase = activeLyricsBinding == null ? 0 : LyricsTiming.Origin(current, activeLyricsBinding.Domain);
        double local = activeLyricsBinding?.Delay ?? 0;
        if (lyricDraft?.AppliesTo(current.Id, activeLyricsBinding) == true) local = lyricDraft.Delay;
        lyricOffset = globalLyricDelay + local;
        LyricsCanvas.CanSeek = raw => LyricsTiming.CanSeek(raw + lyrics.FileOffset + lyricOffset - lyricBase, current.Duration);
        LyricsCanvas.Update(DisplayPosition + lyricBase, lyricOffset);
        UpdateDesktopLyrics();
        correctionStatusRefresh?.Invoke();
    }
    private async Task SeekLyric(double raw)
    {
        if (current == null || !lyrics.Timed) return;
        double seconds = raw + lyrics.FileOffset + lyricOffset - lyricBase;
        if (!LyricsTiming.CanSeek(seconds, current.Duration)) { Show("这一行在当前曲目范围之外，不能定位"); return; }
        await Seek(seconds);
    }
    private LyricsCandidate ReadCandidate(LyricsChoice choice, string encoding)
    {
        if (choice.Embedded)
        {
            string? text = MetadataReader.Read(choice.Path).EmbeddedLyrics;
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("内嵌歌词已失效");
            return LyricsSources.FromEmbedded(choice.Path, text);
        }
        return LyricsSources.FromFile(choice.Path, encoding,lyricsExtensions);
    }
    private async Task LoadLyrics(Track track, int token)
    {
        var history = ReadLyricsHistory(track);
        if (history != null)
        {
            var saved = history.Current;
            bool embedded = saved.Source.EndsWith("#embedded", StringComparison.Ordinal);
            var choice = new LyricsChoice("已绑定来源", embedded ? track.Path : saved.Source, embedded);
            try
            {
                var candidate = await Task.Run(() => ReadCandidate(choice, saved.Encoding));
                if (token != generation || closed) return;
                if (LyricsBinding.Create(candidate, saved.Domain).Revision != saved.Revision)
                    Show("歌词来源已更新，当前沿用已确认版本；可在歌词来源中预览并替换");
            }
            catch (Exception ex) { if (token == generation && !closed) Show("歌词来源暂不可用，保留已确认版本。" + ex.Message, true); }
            return;
        }
        var automatic = await Task.Run(() =>
        {
            string? legacy = store.Get("lyrics:" + track.Id);
            if (legacy != null) return LyricsSources.FromFile(legacy);
            string? embedded = MetadataReader.Read(track.Path).EmbeddedLyrics;
            var candidate = string.IsNullOrWhiteSpace(embedded) ? null : LyricsSources.FromEmbedded(track.Path, embedded);
            if (candidate?.Lyrics.Timed == true) return candidate;
            string sidecar = Path.ChangeExtension(track.Path, ".lrc");
            return File.Exists(sidecar) ? LyricsSources.FromFile(sidecar) : candidate;
        });
        if (token != generation || closed || automatic == null || ReadLyricsHistory(track) != null) return;
        if (automatic.Warnings.Count > 0) { Show("歌词需要确认编码或时间警告，请打开歌词来源预览", true); return; }
        if (track.CuePath != null) { Show("CUE 歌词需确认单曲或整盘时间，请在歌词来源中选择"); return; }
        double legacyDelay = double.TryParse(store.Get("offset:" + track.Id), CultureInfo.InvariantCulture, out double value) ? value : 0;
        SaveLyricsBinding(track, LyricsBinding.Create(automatic, track.CuePath == null ? LyricsTimeDomain.Track : LyricsTimeDomain.Album, legacyDelay));
    }

    private async void ImportLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (current == null) { Show("请先选择歌曲"); return; }
        var target = current;
        var history = ReadLyricsHistory(target);
        var choices = new List<LyricsChoice>();
        if (history != null)
        {
            bool embedded = history.Current.Source.EndsWith("#embedded", StringComparison.Ordinal);
            choices.Add(new("当前来源（重新读取）", embedded ? target.Path : history.Current.Source, embedded));
            choices.Add(new("当前已确认版本", history.Current.Source, Saved: history.Current));
            choices.AddRange(history.Previous.Select((b, i) => new LyricsChoice($"历史版本 {i + 1} · {b.Encoding}", b.Source, Saved: b)));
        }
        choices.Add(new("内嵌歌词", target.Path, true));
        choices.Add(new("同名 LRC", Path.ChangeExtension(target.Path, ".lrc")));
        var source = new ComboBox { Header = "歌词来源", ItemsSource = choices.ToArray(), HorizontalAlignment = HorizontalAlignment.Stretch };
        var encoding = new ComboBox { Header = "编码", ItemsSource = LyricsSources.Encodings, SelectedItem = history?.Current.Encoding ?? "auto", HorizontalAlignment = HorizontalAlignment.Stretch };
        var domain = new ComboBox { Header = "时间基准", ItemsSource = new[] { "单曲时间", "整盘时间" }, SelectedIndex = history != null ? (int)history.Current.Domain : target.CuePath == null ? 0 : -1, PlaceholderText = "请选择单曲或整盘时间", HorizontalAlignment = HorizontalAlignment.Stretch };
        var import = new Button { Content = "选择本地歌词" };
        var keep = new CheckBox { Content = "沿用本曲校正", IsChecked = false, IsEnabled = history != null };
        var allowWarnings = new CheckBox { Content = "确认警告，按当前预览使用", IsChecked = false, Visibility = Visibility.Collapsed };
        var details = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var preview = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var panel = new StackPanel { Spacing = 12 };
        foreach (var element in new UIElement[] { source, import, encoding, domain, keep, details, allowWarnings, preview }) panel.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "歌词来源 · " + target.Title, Content = new ScrollViewer { Content = panel, MaxHeight = 460 }, PrimaryButtonText = "使用此来源", CloseButtonText = "取消", IsPrimaryButtonEnabled = false };
        LyricsCandidate? candidate = null; LyricsBinding? selectedBinding = null;
        int revision = 0; bool alive = true;
        void Validate() => dialog.IsPrimaryButtonEnabled = selectedBinding != null && domain.SelectedIndex >= 0 && (allowWarnings.Visibility == Visibility.Collapsed || allowWarnings.IsChecked == true);
        void Preview()
        {
            selectedBinding = source.SelectedItem is LyricsChoice { Saved: { } saved } ? saved :
                candidate != null && domain.SelectedIndex >= 0 ? LyricsBinding.Create(candidate, (LyricsTimeDomain)domain.SelectedIndex) : null;
            if (selectedBinding == null) { Validate(); return; }
            selectedBinding = selectedBinding with { Delay = keep.IsChecked == true ? history?.Current.Delay ?? 0 : 0 };
            var binding = selectedBinding;
            preview.Text = string.Join("\n\n", binding.Snapshot.Lines.Take(80).Select(line => binding.Snapshot.Timed
                ? $"原始 {line.Time:0.000} s → 单曲 {LyricsTiming.DisplayTime(line.Time, target, binding, globalLyricDelay):0.000} s\n{line.Text}" : line.Text));
            int outside = binding.Snapshot.Timed ? binding.Snapshot.Lines.Count(line => !LyricsTiming.CanSeek(LyricsTiming.DisplayTime(line.Time, target, binding, globalLyricDelay), target.Duration)) : 0;
            details.Text = $"{binding.Source}\n{binding.Encoding} · {(binding.Snapshot.Timed ? "同步歌词" : "纯文本")} · {binding.Snapshot.Lines.Count} 行\n文件原始 offset：{binding.Snapshot.RawOffset ?? "无"}\n文件归一化校正：{DelayText(binding.Snapshot.FileOffset)}" +
                (binding.Snapshot.Lines.Count > 80 ? "\n预览前 80 行" : "") +
                (outside > 0 ? $"\n{outside} 行在曲目范围之外，仅供浏览" : "") +
                (candidate?.Warnings.Count > 0 ? "\n" + string.Join("\n", candidate.Warnings) : "");
            Validate();
        }
        async Task Reload()
        {
            int request = ++revision; candidate = null; selectedBinding = null; allowWarnings.IsChecked = false;
            dialog.IsPrimaryButtonEnabled = false; preview.Text = ""; details.Text = "正在读取歌词";
            if (source.SelectedItem is not LyricsChoice choice) return;
            encoding.IsEnabled = !choice.Embedded && choice.Saved == null; domain.IsEnabled = choice.Saved == null;
            if (choice.Saved != null) { domain.SelectedIndex = (int)choice.Saved.Domain; allowWarnings.Visibility = Visibility.Collapsed; Preview(); return; }
            string selectedEncoding = encoding.SelectedItem as string ?? "auto";
            try
            {
                var loaded = await Task.Run(() => ReadCandidate(choice, selectedEncoding));
                if (!alive || request != revision) return;
                candidate = loaded; allowWarnings.Visibility = loaded.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed; Preview();
            }
            catch (Exception ex) { if (alive && request == revision) { details.Text = ex.Message; dialog.IsPrimaryButtonEnabled = false; } }
        }
        source.SelectionChanged += async (_, _) => await Reload(); encoding.SelectionChanged += async (_, _) => await Reload();
        domain.SelectionChanged += (_, _) => Preview(); keep.Checked += (_, _) => Preview(); keep.Unchecked += (_, _) => Preview();
        allowWarnings.Checked += (_, _) => Validate(); allowWarnings.Unchecked += (_, _) => Validate();
        import.Click += async (_, _) =>
        {
            try
            {
                var file = await Picker(new[]{".lrc",".txt"}.Concat(lyricsExtensions.Where(e=>e.Enabled).SelectMany(e=>e.Extensions)).ToArray()).PickSingleFileAsync(); if (file == null || !alive) return;
                var choice = new LyricsChoice(Path.GetFileName(file.Path), file.Path); choices.Add(choice); source.ItemsSource = choices.ToArray(); source.SelectedItem = choice;
            }
            catch (Exception ex) { details.Text = ex.Message; }
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral(); args.Cancel = true;
            var binding = selectedBinding; var choice = source.SelectedItem as LyricsChoice;
            dialog.IsEnabled = false;
            try
            {
                if (binding == null || choice == null) return;
                if (choice.Saved == null)
                {
                    var fresh = await Task.Run(() => ReadCandidate(choice, binding.Encoding));
                    if (LyricsBinding.Create(fresh, binding.Domain).Revision != binding.Revision) throw new IOException("来源在预览后已变化，请重新选择来源并预览");
                }
                SaveLyricsBinding(target, binding); Show("歌词来源已保存 · " + target.Title); args.Cancel = false;
            }
            catch (Exception ex) { details.Text = ex.Message; }
            finally { dialog.IsEnabled = true; deferral.Complete(); }
        };
        source.SelectedIndex = 0;
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { Show(ex.Message, true); }
        finally { alive = false; ++revision; }
    }

    private static string DelayText(double seconds) => seconds == 0 ? "0 ms" : $"{(seconds > 0 ? "延后" : "提前")} {Math.Abs(seconds * 1000):0.###} ms";
    private async void CorrectLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (current == null || activeLyricsBinding == null || !lyrics.Timed) { Show("当前没有可校正的同步歌词"); return; }
        var target = current; var binding = activeLyricsBinding;
        var draft = new LyricCorrectionDraft(target.Id, binding); lyricDraft = draft;
        var value = new NumberBox { Header = "本曲校正（毫秒；正数延后）", Minimum = -5000, Maximum = 5000, SmallChange = 50, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var advanced = new CheckBox { Content = "高级范围", IsChecked = Math.Abs(binding.Delay) > 5 };
        var confirm = new CheckBox { Content = "确认使用超过 5000 ms 的校正", IsChecked = false };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var reset = new Button { Content = "重置本曲" }; reset.Click += (_, _) => value.Value = 0;
        var steps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var earlier = new Button { Content = "提前 50 ms" }; earlier.Click += (_, _) => value.Value = Math.Max(value.Minimum, (double.IsFinite(value.Value) ? value.Value : 0) - 50);
        var later = new Button { Content = "延后 50 ms" }; later.Click += (_, _) => value.Value = Math.Min(value.Maximum, (double.IsFinite(value.Value) ? value.Value : 0) + 50);
        steps.Children.Add(earlier); steps.Children.Add(later);
        var panel = new StackPanel { Spacing = 14 };
        foreach (var element in new UIElement[] { value, steps, advanced, confirm, reset, status, error }) panel.Children.Add(element);
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "歌词校正 · " + target.Title, Content = new ScrollViewer { Content = panel, MaxHeight = 430 }, PrimaryButtonText = "保存本曲校正", CloseButtonText = "放弃并关闭" };
        void RefreshStatus()
        {
            bool changedTrack = current?.Id != target.Id;
            status.Text = $"全局：{DelayText(globalLyricDelay)}\n文件：{DelayText(binding.Snapshot.FileOffset)}\n本曲：{DelayText(draft.Delay)}\n合计：{DelayText(globalLyricDelay + binding.Snapshot.FileOffset + draft.Delay)}\n" +
                (changedTrack ? "已经切歌；草稿仍属于「" + target.Title + "」，等待保存或放弃" : draft.Dirty ? "试用中" : "已保存值");
            confirm.Visibility = Math.Abs(draft.Delay) > 5 ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = double.IsFinite(value.Value) && (Math.Abs(draft.Delay) <= 5 || confirm.IsChecked == true);
        }
        void Range() { value.Minimum = advanced.IsChecked == true ? -86400000 : -5000; value.Maximum = advanced.IsChecked == true ? 86400000 : 5000; }
        advanced.Checked += (_, _) => Range(); advanced.Unchecked += (_, _) => Range(); Range(); value.Value = binding.Delay * 1000;
        value.ValueChanged += (_, _) => { if (double.IsFinite(value.Value)) draft.Delay = value.Value / 1000; UpdateLyricsTiming(); RefreshStatus(); };
        confirm.Checked += (_, _) => RefreshStatus(); confirm.Unchecked += (_, _) => RefreshStatus();
        correctionStatusRefresh = RefreshStatus; RefreshStatus();
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try
            {
                var latest = ReadLyricsHistory(target)?.Current ?? throw new InvalidOperationException("歌词来源已失效");
                SaveLyricsBinding(target, draft.Commit(latest));
            }
            catch (Exception ex) { args.Cancel = true; error.Text = ex.Message; }
        };
        try { await dialog.ShowAsync(); }
        catch (Exception ex) { Show(ex.Message, true); }
        finally { lyricDraft = null; correctionStatusRefresh = null; UpdateLyricsTiming(); }
    }
}
