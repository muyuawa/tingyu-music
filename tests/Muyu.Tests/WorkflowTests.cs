using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Muyu.Core;

internal static class WorkflowTests
{
    public static void Run(string fixtureRoot, Action<bool, string> check)
    {
        string root = Path.Combine(fixtureRoot, "workflows-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IOException) { rejected = true; }
            check(rejected, message);
        }
        string source = Path.Combine(root, "lyrics.lrc");
        File.WriteAllText(source, "[offset:-100]\n[00:42.000]歌词", new UTF8Encoding(false));
        var candidate = LyricsSources.FromFile(source);
        var binding = LyricsBinding.Create(candidate, LyricsTimeDomain.Track, .25);
        check(binding.Snapshot.RawOffset == "-100" && binding.Snapshot.FileOffset == .1, "Raw offset metadata remains distinct from normalized product direction");
        var track = new Track("one", Path.Combine(root, "music.wav"), "A", "Artist", "Album", 1000, 16, 2, 240000);
        check(Math.Abs(LyricsTiming.DisplayTime(42, track, binding, 0) - 42.35) < .000001, "B05 normalized file and track delays match 42.350 example");
        var cue = track with { Id = "cue", StartFrame = 150000, EndFrame = 200000, CuePath = Path.Combine(root, "album.cue") };
        var albumBinding = LyricsBinding.Create(candidate, LyricsTimeDomain.Album, .25);
        check(Math.Abs(LyricsTiming.DisplayTime(192, cue, albumBinding, 0) - 42.35) < .000001, "B05 album time subtracts CUE source origin before offsets");
        check(Math.Abs(LyricsTiming.DisplayTime(42, cue, binding, .5) - 42.85) < .000001, "Track-domain CUE lyrics do not subtract album origin");
        check(!LyricsTiming.CanSeek(-.1, 50) && !LyricsTiming.CanSeek(50, 50) && !LyricsTiming.CanSeek(double.NaN, 50) && LyricsTiming.CanSeek(49.999, 50), "Out-of-range lyric times cannot seek or extend playback");
        check(binding.Revision != albumBinding.Revision, "Lyrics revision includes time domain");
        check(binding.Revision == LyricsBinding.Create(LyricsSources.FromFile(source), LyricsTimeDomain.Track).Revision, "Identical source keeps stable revision");
        string replacement = Path.Combine(root, "replacement.lrc"); File.Copy(source, replacement); File.Move(replacement, source, true);
        check(LyricsBinding.Create(LyricsSources.FromFile(source), LyricsTimeDomain.Track).Revision != binding.Revision, "Replacing a file with identical bytes changes its source identity");
        var frozen = binding.Snapshot;
        File.WriteAllText(source, "[00:42]new source");
        var changed = LyricsBinding.Create(LyricsSources.FromFile(source), LyricsTimeDomain.Track);
        check(changed.Revision != binding.Revision && changed.Delay == 0 && frozen.Lines[0].Text == "歌词", "Changed source cannot silently inherit correction or replace frozen text");
        var history = new LyricsBindingHistory(binding, []).Replace(changed);
        var saved = JsonSerializer.Deserialize<LyricsBindingHistory>(JsonSerializer.Serialize(history))!;
        check(saved.Previous[0].Revision == binding.Revision && saved.Previous[0].Delay == .25 && saved.Current.Delay == 0, "Source history and correction survive persistence");
        var draft = new LyricCorrectionDraft(track.Id, binding) { Delay = -.75 };
        check(binding.Delay == .25 && draft.Dirty && draft.Commit(binding).Delay == -.75, "Correction trial does not persist until explicit commit");
        check(!draft.AppliesTo(cue.Id, binding) && !draft.AppliesTo(track.Id, changed) && draft.AppliesTo(track.Id, binding), "Correction trial is pinned to original track and revision");
        Reject(() => draft.Commit(changed), "Stale lyric correction commit rejected");
        draft.Delay = double.NaN; Reject(() => draft.Commit(binding), "Nonfinite lyric correction rejected");
        check(new LyricCorrectionDraft(track.Id, binding).Delay == .25, "Discarded trial restores persisted delay");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        foreach (string encoding in new[] { "gb18030", "shift_jis", "utf-16", "utf-16BE" })
        {
            string path = Path.Combine(root, encoding + ".lrc"); string text = "[00:01]日本語";
            File.WriteAllText(path, text, Encoding.GetEncoding(encoding));
            check(LyricsSources.FromFile(path, encoding).Lyrics.Lines[0].Text == "日本語", "Explicit lyrics encoding " + encoding);
        }
        string unicode = Path.Combine(root, "utf-16.lrc");
        check(LyricsSources.FromFile(unicode).Encoding == "utf-16", "Automatic lyrics encoding honors UTF-16 BOM");
        File.WriteAllBytes(source, [0x81, 0x81, 0xFF]); Reject(() => LyricsSources.FromFile(source), "Invalid UTF-8 is diagnosed without replacement decoding");
        File.WriteAllText(source, "[00:01]<00:01.20>word");
        var words = LyricsSources.FromFile(source);
        check(words.Lyrics.Lines[0].Text == "word" && words.Lyrics.Lines[0].Words?.Count==1, "Enhanced word timestamps retain reliable line text and timing");
        check(LrcParser.Warnings("[00:20]later\n[00:10]earlier\n[00:99]bad").Count == 2, "Invalid and nonmonotonic lyric times are reported");
        File.WriteAllText(source, "plain lyrics"); check(!LyricsSources.FromFile(source).Lyrics.Timed, "Plain source keeps no synthetic current line");
        File.WriteAllBytes(source, new byte[4 * 1024 * 1024 + 1]); Reject(() => LyricsSources.FromFile(source), "Oversized lyrics rejected before decoding");

        var queue = new[] { new QueueItem(Guid.NewGuid(), track), new QueueItem(Guid.NewGuid(), cue), new QueueItem(Guid.NewGuid(), cue) };
        var selection = PlaylistSelection.FromQueue(queue, 1, false);
        check(selection.Length == 2 && PlaylistSelection.Duplicates(selection) == 1, "Queue save defaults to current and upcoming with duplicate count");
        check(PlaylistSelection.FromQueue(queue, 1, true).Length == 3 && PlaylistSelection.FromQueue([], -1, false).Length == 0, "Queue save history opt-in and empty selection");
        check(PlaylistSelection.ValidateName("  Evening  ", []) == "Evening", "Playlist name normalized without modifying contents");
        Reject(() => PlaylistSelection.ValidateName("evening", ["Evening"]), "Playlist name collision is case-insensitive");
        Reject(() => PlaylistSelection.ValidateName("bad\nname", []), "Playlist name rejects control characters");
        check(PlaylistSelection.ValidateName("EVENING", ["Evening"], "Evening") == "EVENING", "Playlist can change only name casing");
        var store = new LibraryStore(Path.Combine(root, "database"));
        store.SetMany(new Dictionary<string, string> { ["playlists"] = "names", ["playlist-track-snapshots"] = "tracks" });
        check(store.Get("playlists") == "names" && store.Get("playlist-track-snapshots") == "tracks", "Playlist names and recoverable references persist together");
        Reject(() => store.SetMany(new Dictionary<string, string> { ["playlists"] = "changed", ["invalid"] = null! }), "Invalid settings batch is rejected");
        check(store.Get("playlists") == "names", "Failed settings batch rolls back earlier writes");

        string destination = Path.Combine(root, "export.m3u8");
        bool Exists(string _) => true;
        var ordinary = PlaylistExport.Preview([track, track], CueExportPolicy.Unspecified, PlaylistPathStyle.Relative, false, destination, Exists);
        check(ordinary.CanWrite && ordinary.Written == 2 && ordinary.Rows.All(r => r.OutputPath == "music.wav"), "M3U8 preserves ordinary repeats and computes relative paths");
        check(!PlaylistExport.Preview([cue], CueExportPolicy.Unspecified, PlaylistPathStyle.Relative, false).CanWrite, "CUE export requires explicit compatibility choice");
        var skipCue = PlaylistExport.Preview([cue, track], CueExportPolicy.Skip, PlaylistPathStyle.Absolute, false, exists: Exists);
        check(skipCue.Written == 1 && skipCue.Skipped == 1 && skipCue.Rows[1].OutputPath == track.Path, "CUE skip retains only ordinary files");
        check(!PlaylistExport.Preview([cue], CueExportPolicy.Skip, PlaylistPathStyle.Relative, false).CanWrite, "All-CUE playlist cannot export an empty general playlist");
        var whole = PlaylistExport.Preview([cue, cue with { Id = "cue2", StartFrame = 200000, EndFrame = 240000 }], CueExportPolicy.WholeSource, PlaylistPathStyle.Absolute, false, exists: Exists);
        check(whole.Written == 1 && whole.Skipped == 1 && whole.Rows[0].Duration == 240 && whole.Rows[0].Title == "music", "Whole-source export emits one full duration, not repeated segment paths");
        check(PlaylistExport.Preview([track, cue, track], CueExportPolicy.WholeSource, PlaylistPathStyle.Absolute, false, exists: Exists).Written == 1, "Whole-source dedup includes physical entries for that CUE source");
        var missing = PlaylistExport.Preview([track], CueExportPolicy.Skip, PlaylistPathStyle.Relative, false, destination, _ => false);
        check(missing.Written == 1 && missing.Warnings == 1, "Missing file path retained with warning by default");
        check(!PlaylistExport.Preview([track], CueExportPolicy.Skip, PlaylistPathStyle.Relative, true, destination, _ => false).CanWrite, "Missing files skipped only by explicit choice");
        var online = track with { Path = "https://example.invalid/music.flac" };
        var mixed = PlaylistExport.Preview([online, track], CueExportPolicy.Skip, PlaylistPathStyle.Absolute, false, exists: Exists);
        check(mixed.Written == 1 && mixed.Skipped == 1 && mixed.Rows[0].Warning, "Online URL skipped without download");
        var hashName = track with { Path = Path.Combine(root, "#EXTM3U.wav"), Title = "title\n#EXTINF:0,injected" };
        var hashPlan = PlaylistExport.Preview([hashName], CueExportPolicy.Skip, PlaylistPathStyle.Relative, false, destination, Exists);
        string rendered = PlaylistExport.Render(hashPlan);
        check(hashPlan.Rows[0].OutputPath!.StartsWith(".\\#") && rendered.Split('\n').Count(l => l.StartsWith("#EXTINF:")) == 1, "M3U comments cannot be injected via filename or title");
        check(PlaylistExport.Preview([track with { Path = "D:\\bad\npath.wav" }], CueExportPolicy.Skip, PlaylistPathStyle.Absolute, false, exists: Exists).Skipped == 1, "Newline in file path is rejected");
        string differentRoot = Path.GetPathRoot(root)!.StartsWith("Z:", StringComparison.OrdinalIgnoreCase) ? "Y:\\elsewhere.wav" : "Z:\\elsewhere.wav";
        var crossVolume = PlaylistExport.Preview([track with { Path = differentRoot }], CueExportPolicy.Skip, PlaylistPathStyle.Relative, false, destination, Exists);
        check(crossVolume.Rows[0].OutputPath == differentRoot && crossVolume.Warnings == 1, "Cross-volume relative export uses explicit absolute-path warning");
        var initial = PlaylistExport.Preview([track, track with { Path = differentRoot }], CueExportPolicy.Skip, PlaylistPathStyle.Relative, false, exists: Exists);
        var relocated = PlaylistExport.AtDestination(initial, PlaylistPathStyle.Relative, destination);
        check(relocated.Written == initial.Written && relocated.Skipped == initial.Skipped && relocated.Rows[0].OutputPath == "music.wav" && relocated.Rows[1].Warning, "Choosing export location retains preview membership and adds path warnings");
        PlaylistExport.Write(destination, ordinary);
        var bytes = File.ReadAllBytes(destination);
        check(!bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && Encoding.UTF8.GetString(bytes) == PlaylistExport.Render(ordinary), "M3U8 writes verified UTF-8 text");
        PlaylistExport.Write(destination, whole);
        check(File.ReadAllText(destination) == PlaylistExport.Render(whole), "M3U8 replaces existing export after temporary write");
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination)));
        using (var locked = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => PlaylistExport.Write(destination, ordinary), "Export failure leaves existing destination intact");
        check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))) == before && !Directory.EnumerateFiles(root, ".muyu-*.tmp").Any(), "Failed export preserves original bytes and cleans its temporary file");
        Reject(() => PlaylistExport.Write(Path.Combine(root, "music.wav"), ordinary), "Export cannot target an audio file extension");
        PlaylistExport.Write(Path.Combine(root, "mixed.m3u8"), mixed);
        check(File.ReadAllText(Path.Combine(root, "mixed.m3u8")).Contains(track.Path), "Skipped online entries do not break export writing");
    }
}
