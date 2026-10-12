using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Muyu.Core;

internal static class LibraryRecoveryTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    public static void Run(string fixtures, Action<bool, string> check)
    {
        string area = Path.Combine(fixtures, "recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(area);
        string reference = Path.Combine(fixtures, "reference-16.wav");
        string Copy(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(area, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.Copy(reference, path); return path;
        }
        Track TrackAt(string path)
        {
            var info = NativeAudio.Probe(path);
            return new(Track.Identity(path), path, Path.GetFileNameWithoutExtension(path), "Artist", "Album", info.SampleRate, info.Bits, info.Channels, info.Frames);
        }
        IReadOnlyList<Track> Add(LibraryStore db, IReadOnlyList<Track> tracks, string? root = null) =>
            db.AddBatch(tracks, root, tracks.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p => p, p => LocalFileIdentity.Capture(p), StringComparer.OrdinalIgnoreCase));
        void Reject(Action action, string message)
        {
            bool rejected = false;
            try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or IOException or InvalidDataException or JsonException) { rejected = true; }
            check(rejected, message);
        }

        string a = Copy("scope/a.wav"), b = Copy("scope/child/b.wav"), scope = Path.GetDirectoryName(a)!;
        File.WriteAllText(Path.Combine(scope, "notes.txt"), "ignored media");
        check(LibraryScope.Contains(scope, b) && !LibraryScope.Contains(scope, b, false) && LibraryScope.Contains(scope, a, false), "B01 recursive and direct-child scope boundaries");
        check(!LibraryScope.Contains(scope, scope + "-other/a.wav"), "Sibling path prefix does not imply root ownership");
        check(LibraryScope.Contains(Path.GetPathRoot(a)!, a) && LibraryScope.Normalize(scope + "\\") == scope, "Drive root and trailing separator normalization");
        Reject(() => LibraryScope.ValidateDirectory(a), "Audio file cannot be registered as a directory");
        Reject(() => LibraryScope.ValidateDirectory(Path.Combine(area, "absent")), "Missing root cannot be registered");
        var shallow = LibraryScanner.Scan([scope], CancellationToken.None, recursive: false);
        check(shallow.Tracks.Count == 1 && shallow.CheckedFiles == 2, "Shallow scan excludes children and counts examined non-audio files");
        check(LibraryScanner.Scan([scope], CancellationToken.None).Tracks.Count == 2, "Recursive scan includes child media");

        var store = new LibraryStore(Path.Combine(area, "library"));
        var parent = store.RegisterRoot(scope, true, true); var child = store.RegisterRoot(Path.GetDirectoryName(b)!, true, false);
        check(store.RegisterRoot(scope.ToUpperInvariant() + "\\", true, true).Id == parent.Id && store.Roots().Count == 2, "Repeated normalized root retains identity and scan preferences");
        var ta = TrackAt(a); var tb = TrackAt(b);
        Add(store, [ta, tb], parent.Id); Add(store, [tb], child.Id);
        check(store.ReadAll().Count == 2 && store.RootTrackCount(parent.Id) == 2 && store.RootTrackCount(child.Id) == 1, "Overlapping roots share one index and independent ownership");
        store.Set("favorites", JsonSerializer.Serialize(new[] { tb.Id }));
        store.RegisterRoot(scope, false, false);
        check(store.RootTrackCount(parent.Id) == 1 && store.ReadAll().Count == 2, "Narrowing scope releases child ownership while preserving overlapping root");
        Reject(() => Add(store, [tb], parent.Id), "Batch from old recursive scan cannot reattach excluded child path");
        store.RemoveRoot(child.Id);
        check(store.ReadAll().Count == 1 && !store.ReadAll(true).Single(t => t.Id == tb.Id).InLibrary && File.Exists(b), "Removing final root archives track without deleting source");
        check(store.Get("favorites")!.Contains(tb.Id), "Removing root preserves favorite identity");
        var readded = store.RegisterRoot(Path.GetDirectoryName(b)!, true, false); Add(store, [tb], readded.Id);
        check(store.ReadAll().Any(t => t.Id == tb.Id), "Reimporting verified source restores archived identity");
        Add(store, [tb]); store.RemoveRoot(readded.Id);
        check(store.ReadAll().Any(t => t.Id == tb.Id), "Explicit file import remains after monitored root removal");
        var removedRoot = store.RegisterRoot(Path.Combine(area, "scope", "child"), true, false); store.RemoveRoot(removedRoot.Id);
        Reject(() => Add(store, [tb], removedRoot.Id), "Late scan batch cannot recreate a removed root");

        string link = Path.Combine(scope, "hardlink.wav");
        check(CreateHardLinkW(link, a, IntPtr.Zero), "Isolated hard-link fixture created");
        var aliases = new LibraryStore(Path.Combine(area, "aliases")); Add(aliases, [TrackAt(a), TrackAt(link)]);
        check(aliases.ReadAll().Count == 1 && LocalFileIdentity.Capture(a).Identity == LocalFileIdentity.Capture(link).Identity, "One batch deduplicates physical file identity across hard links");
        string separate = Copy("separate/a.wav"); Add(aliases, [TrackAt(separate)]);
        check(aliases.ReadAll().Count == 2, "Separate copies remain independent even with identical names and bytes");
        byte[] changedAlias = File.ReadAllBytes(link); changedAlias[^1] ^= 1; File.WriteAllBytes(link, changedAlias); Add(aliases, [TrackAt(link)]);
        check(aliases.ReadAll().Count == 2 && !aliases.ReadAll().Any(t => t.Id == ta.Id), "Rescanning changed hard-link alias archives old physical source without duplicate active index");
        Reject(() => aliases.ValidateSource(ta), "Hard-link content change cannot inherit old queue position");
        File.WriteAllBytes(a, File.ReadAllBytes(reference));

        string replacePath = Copy("replacement/track.wav"); var replaceStore = new LibraryStore(Path.Combine(area, "replacement-db"));
        var original = TrackAt(replacePath); Add(replaceStore, [original]); var originalEvidence = replaceStore.TrackFingerprints()[original.Id];
        string newFile = Copy("replacement/new.wav"); File.Move(newFile, replacePath, true); Add(replaceStore, [TrackAt(replacePath)]);
        check(replaceStore.ReadAll().Count == 1 && replaceStore.ReadAll()[0].Id != original.Id && !replaceStore.ReadAll(true).Single(t => t.Id == original.Id).InLibrary, "Same path with new file identity creates new track and archives old identity");
        check(replaceStore.TrackFingerprints()[original.Id] == originalEvidence, "Superseded track keeps its own original source evidence");
        Reject(() => replaceStore.ValidateSource(original), "Old queue cannot silently resume a replaced file with identical bytes");
        replaceStore.ValidateSource(replaceStore.ReadAll()[0]); check(true, "Newly confirmed track validates its current source");

        string mutableRoot = Path.Combine(area, "mutable-root"); Directory.CreateDirectory(mutableRoot);
        var identityStore = new LibraryStore(Path.Combine(area, "identity-db")); identityStore.RegisterRoot(mutableRoot, true, false);
        Directory.Move(mutableRoot, mutableRoot + "-old"); Directory.CreateDirectory(mutableRoot);
        Reject(() => identityStore.RegisterRoot(mutableRoot, true, false), "Replacing directory at same path requires explicit root recovery");

        string outside = Copy("outside.wav"), inside = Copy("cue-scope/inside.wav"); string cueScope = Path.GetDirectoryName(inside)!;
        File.WriteAllText(Path.Combine(cueScope, "external.cue"), "FILE \"../outside.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:00");
        var external = LibraryScanner.Scan([cueScope], CancellationToken.None);
        check(external.Tracks.Count == 1 && external.Tracks[0].Path == inside && external.Issues!.Any(i => i.Kind == "cue"), "CUE reference outside confirmed root is rejected without reading external media");
        File.WriteAllText(Path.Combine(cueScope, "broken.cue"), "FILE \"inside.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:30\nTRACK 02 AUDIO\nINDEX 01 00:00:20");
        var broken = LibraryScanner.Scan([cueScope], CancellationToken.None);
        check(broken.Tracks.Count == 1 && broken.Tracks[0].CuePath == null && broken.Issues!.Count == 2, "Invalid CUE retains playable whole-source fallback and structured errors");

        string batchRoot = Path.Combine(area, "cancel-batch");
        for (int i = 0; i < 35; i++) Copy($"cancel-batch/{i:00}.wav");
        var cancelledStore = new LibraryStore(Path.Combine(area, "cancel-db")); var batchOwner = cancelledStore.RegisterRoot(batchRoot, true, false);
        using (var cancellation = new CancellationTokenSource())
        {
            ScanResult? partial = null;
            try { LibraryScanner.Scan([batchRoot], cancellation.Token, commit: batch => { Add(cancelledStore, batch, batchOwner.Id); cancellation.Cancel(); }); }
            catch (ScanInterruptedException ex) { partial = ex.PartialResult; }
            check(partial != null && partial.CheckedFiles == 35 && cancelledStore.ReadAll().Count == 32, "Cancellation preserves committed transaction and accurate partial scan report");
        }

        var one = new QueueItem(Guid.NewGuid(), ta); var repeated = new QueueItem(Guid.NewGuid(), ta);
        var snapshot = new PlaybackSession(1, 1, [one, repeated], repeated.ItemId, 1234, 2, true, [one.ItemId, repeated.ItemId]);
        var sessionStore = new LibraryStore(Path.Combine(area, "sessions")); sessionStore.SaveSession(snapshot);
        var recovered = new LibraryStore(Path.Combine(area, "sessions")).ReadSession().Session!;
        check(recovered.Items.Select(i => i.ItemId).SequenceEqual(new[] { one.ItemId, repeated.ItemId }) && recovered.CurrentItemId == repeated.ItemId && recovered.Position == 1234, "Atomic session preserves duplicate entries, exact sample position and current identity");
        check(recovered.Repeat == 2 && recovered.Shuffle && recovered.OriginalOrder!.SequenceEqual(snapshot.OriginalOrder!), "Session restores repeat, shuffle and original order without an audio command");
        check((snapshot with { Position = long.MaxValue }).Validate().Position == ta.Frames, "Recovered position clamps to track boundary");
        sessionStore.SaveSession(snapshot with { Revision = 2, Position = 4567 }); string validSession = sessionStore.Get("playback-session")!;
        Reject(() => sessionStore.SaveSession(snapshot), "Stale session cannot overwrite newer queue or position");
        check(sessionStore.Get("playback-session") == validSession, "Rejected stale save leaves complete session intact");
        sessionStore.Set("playback-session", "corrupt"); var backup = sessionStore.ReadSession();
        check(backup.Session!.Position == 1234 && backup.Warning != null && sessionStore.Get("playback-session") == "corrupt", "Corrupt main session falls back without overwriting diagnostic record");
        sessionStore.SaveSession(snapshot with { Revision = 3 });
        check(sessionStore.ReadSession().Session!.Revision == 3 && sessionStore.Get("playback-session-backup") != null, "New valid session can replace corrupt main snapshot while preserving backup");
        Reject(() => (snapshot with { Items = [one, one] }).Validate(), "Duplicate queue item identities are rejected");
        Reject(() => (snapshot with { CurrentItemId = Guid.NewGuid() }).Validate(), "Missing current identity is rejected");
        Reject(() => (snapshot with { Items = null! }).Validate(), "Null queue snapshot is rejected as recoverable format error");
        Reject(() => (snapshot with { Items = [one with { ItemId = Guid.Empty }] }).Validate(), "Empty queue identity is rejected");
        Reject(() => (snapshot with { Items = [one with { Track = ta with { EndFrame = -2 } }] }).Validate(), "Invalid negative end sentinel is rejected");
        Reject(() => (snapshot with { Items = [one with { Track = ta with { Path = "" } }] }).Validate(), "Missing source path cannot restore as a playable track");
        Reject(() => (snapshot with { OriginalOrder = [one.ItemId, one.ItemId] }).Validate(), "Corrupt original shuffle order is rejected");
        var legacyStore = new LibraryStore(Path.Combine(area, "legacy-session"));
        legacyStore.Set("queue-v2", JsonSerializer.Serialize(new[] { one, repeated })); legacyStore.Set("queue-index", "1");
        check(legacyStore.ReadSession().Session!.CurrentItemId == repeated.ItemId && legacyStore.ReadSession().Session!.Position == 0, "Legacy queue migrates conservatively without inventing old playback position");
        legacyStore.Set("queue-v2", "broken");
        check(legacyStore.ReadSession().Session == null && legacyStore.Get("queue-v2") == "broken", "Invalid legacy queue remains available for diagnosis");
        Reject(() => (snapshot with { Items = [one with { Track = ta with { SampleRate = 1, TotalFrames = long.MaxValue } }] }).Validate(), "Corrupt duration cannot overflow restored time display");

        string migration = Path.Combine(area, "migration-db"); Directory.CreateDirectory(migration);
        using (var oldDb = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(migration, "library.db") }.ToString()))
        {
            oldDb.Open(); using var command = oldDb.CreateCommand(); command.CommandText = """
                CREATE TABLE tracks(id TEXT PRIMARY KEY,path TEXT NOT NULL,title TEXT,artist TEXT,album TEXT,rate INTEGER,bits INTEGER,channels INTEGER,total INTEGER,start INTEGER,end INTEGER,cue TEXT);
                CREATE TABLE settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
                INSERT INTO tracks VALUES($id,$path,'Legacy','Artist','Album',44100,16,2,44100,0,-1,NULL);
                INSERT INTO settings VALUES('favorites',$favorites);
                """;
            command.Parameters.AddWithValue("$id", ta.Id); command.Parameters.AddWithValue("$path", a); command.Parameters.AddWithValue("$favorites", JsonSerializer.Serialize(new[] { ta.Id })); command.ExecuteNonQuery();
        }
        var migrated = new LibraryStore(migration);
        check(migrated.ReadAll().Single().Id == ta.Id && migrated.Get("favorites")!.Contains(ta.Id), "Legacy database migration preserves visible tracks and reference settings");
        check(Directory.GetFiles(migration, "library-before-v04-*.db").Length == 1, "Schema migration creates independent database backup");
        _ = new LibraryStore(migration);
        check(Directory.GetFiles(migration, "library-before-v04-*.db").Length == 1, "Already-migrated database does not create repeated backups");

        string oldPath = Copy("relocate/album.wav"), secondPath = Copy("relocate/disc2.wav"), movedPath = Path.Combine(area, "moved", "album.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(movedPath)!);
        var whole = TrackAt(oldPath); string cuePath = Path.Combine(area, "relocate", "album.cue"); long middle = whole.TotalFrames / 2;
        var first = whole with { Id = Track.Identity(oldPath, 0, middle), EndFrame = middle, CuePath = cuePath };
        var last = whole with { Id = Track.Identity(oldPath, middle, whole.TotalFrames), StartFrame = middle, EndFrame = whole.TotalFrames, CuePath = cuePath };
        var other = TrackAt(secondPath) with { CuePath = cuePath };
        var relocationStore = new LibraryStore(Path.Combine(area, "relocation-db")); Add(relocationStore, [first, last, other]);
        var q1 = new QueueItem(Guid.NewGuid(), first); var q2 = new QueueItem(Guid.NewGuid(), first);
        var relocationSession = new PlaybackSession(1, 1, [q1, q2], q2.ItemId, 4321);
        relocationStore.SaveSession(relocationSession); relocationStore.SaveSession(relocationSession with { Revision = 2 });
        relocationStore.Set("playlist-track-snapshots", JsonSerializer.Serialize(new Dictionary<string, Track> { [first.Id] = first, [last.Id] = last }));
        relocationStore.Set("favorites", JsonSerializer.Serialize(new[] { first.Id })); relocationStore.Set("lyrics-" + first.Id, "bound revision");
        File.Move(oldPath, movedPath);
        var related = relocationStore.ReadAll(true);
        var candidates = Relocation.Find([first, last], relocationStore.Fingerprints([first, last]), [movedPath], CancellationToken.None);
        check(candidates.Count == 1 && candidates[0].Verified, "Relocation verifies full content and every segment of moved source");
        string ambiguous = Path.Combine(area, "moved", "copy.wav"); File.Copy(movedPath, ambiguous);
        check(Relocation.Find([first, last], relocationStore.Fingerprints([first, last]), [movedPath, ambiguous], CancellationToken.None).Count(c => c.Verified) == 2, "Identical duplicate candidates remain separate for explicit selection");
        Reject(() => Relocation.Prepare(related, [candidates[0], candidates[0]], CancellationToken.None), "Ambiguous mappings cannot be applied together");
        var wrong = Relocation.Find([first], relocationStore.Fingerprints([first]), [Path.Combine(fixtures, "reference-24.wav")], CancellationToken.None);
        check(wrong.Count == 1 && !wrong[0].Verified, "Similar media with different content or format cannot inherit identity");
        check(!Relocation.Find([first], new Dictionary<string, SourceFingerprint>(), [movedPath], CancellationToken.None)[0].Verified, "Legacy source without evidence requires explicit reimport");
        byte[] originalBytes = File.ReadAllBytes(movedPath); byte[] altered = (byte[])originalBytes.Clone(); altered[^1] ^= 1; File.WriteAllBytes(movedPath, altered);
        Reject(() => Relocation.Prepare(related, candidates, CancellationToken.None), "Candidate changed after preview is rejected");
        File.WriteAllBytes(movedPath, originalBytes); candidates = Relocation.Find([first, last], relocationStore.Fingerprints([first, last]), [movedPath], CancellationToken.None);
        File.Move(secondPath, secondPath + ".offline");
        Reject(() => Relocation.Prepare(related, candidates, CancellationToken.None), "Missing secondary CUE source blocks partial reconstruction");
        File.Move(secondPath + ".offline", secondPath);
        var plan = Relocation.Prepare(related, candidates, CancellationToken.None); relocationStore.ApplyRelocation(plan);
        var mapped = relocationStore.ReadAll(true);
        check(mapped.Where(t => t.Id == first.Id || t.Id == last.Id).All(t => t.Path == movedPath) && mapped.Single(t => t.Id == other.Id).Path == secondPath, "Atomic mapping retains segment identities and unchanged CUE sources");
        var saved = relocationStore.ReadSession().Session!;
        check(saved.CurrentItemId == q2.ItemId && saved.Position == 4321 && saved.Items.All(i => i.Track.Path == movedPath), "Mapping preserves exact queue identities, duplicates and sample position");
        check(PlaybackSession.Parse(relocationStore.Get("playback-session-backup")!).Items.All(i => i.Track.Path == movedPath), "Mapping updates backup session so fallback cannot restore stale paths");
        check(JsonSerializer.Deserialize<Dictionary<string, Track>>(relocationStore.Get("playlist-track-snapshots")!)![first.Id].Path == movedPath && relocationStore.Get("favorites")!.Contains(first.Id) && relocationStore.Get("lyrics-" + first.Id) == "bound revision", "Mapping preserves playlist, favorite and lyric binding references");
        relocationStore.ValidateSource(mapped.Single(t => t.Id == first.Id)); check(true, "Mapped source uses newly confirmed identity evidence");
        relocationStore.UndoRelocation();
        check(relocationStore.ReadAll(true).Single(t => t.Id == first.Id).Path == oldPath && !File.Exists(oldPath) && File.Exists(movedPath), "Undo restores only old index path, leaving moved source untouched");
        check(relocationStore.ReadSession().Session!.Position == 4321 && relocationStore.TrackFingerprints()[first.Id].Identity == LocalFileIdentity.Capture(movedPath).Identity, "Undo preserves reliable position and original evidence for a same-file move");
        Reject(() => relocationStore.UndoRelocation(), "Mapping undo is single use");
        var copyCandidate = Relocation.Find([first, last], relocationStore.Fingerprints([first, last]), [ambiguous], CancellationToken.None);
        relocationStore.ApplyRelocation(Relocation.Prepare(relocationStore.ReadAll(true), copyCandidate, CancellationToken.None));
        check(relocationStore.TrackFingerprints()[first.Id].Identity == LocalFileIdentity.Capture(ambiguous).Identity, "Explicit identical copy mapping records new physical source identity");
        relocationStore.UndoRelocation();
        check(relocationStore.TrackFingerprints()[first.Id].Identity == LocalFileIdentity.Capture(movedPath).Identity, "Undo of copy mapping restores original evidence rather than candidate identity");
        var freshPlan = Relocation.Prepare(relocationStore.ReadAll(true), candidates, CancellationToken.None);
        string beforeSession = relocationStore.Get("playback-session")!; relocationStore.Set("playlist-track-snapshots", "invalid-json");
        Reject(() => relocationStore.ApplyRelocation(freshPlan), "Invalid persisted reference aborts whole mapping transaction");
        check(relocationStore.ReadAll(true).Single(t => t.Id == first.Id).Path == oldPath && relocationStore.Get("playback-session") == beforeSession, "Mapping transaction failure rolls back paths and already-updated session together");
        relocationStore.Set("playlist-track-snapshots", "{}"); relocationStore.ApplyRelocation(freshPlan);
        Add(relocationStore, [relocationStore.ReadAll(true).Single(t => t.Id == first.Id) with { Title = "New metadata" }]);
        Reject(() => relocationStore.UndoRelocation(), "New library edit prevents stale undo from overwriting current records");
    }
}
