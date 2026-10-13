using Muyu.Core;
using System.Text.Json;

static class TagEditReferenceTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        foreach (bool playlist in new[] { true, false })
        {
            string folder = Path.Combine(root, "tag-references-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "external.wav");
            File.Copy(Path.Combine(root, "reference-16.wav"), path);
            var info = NativeAudio.Probe(path);
            var original = LocalFileIdentity.Capture(path);
            var track = new Track(Track.SourceIdentity(original, 0, -1), path, "Original", "Artist", "Album",
                info.SampleRate, info.Bits, info.Channels, info.Frames, InLibrary: false, Encoding: info.Encoding);
            var store = new LibraryStore(Path.Combine(folder, "data"));
            store.RememberSources([track], new Dictionary<string, SourceFingerprint> { [path] = original });
            if (playlist) store.Set("playlist-track-snapshots", JsonSerializer.Serialize(new Dictionary<string, Track> { [track.Id] = track }));
            var item = new QueueItem(Guid.NewGuid(), track);
            var session = new PlaybackSession(1, 5, [item], item.ItemId, 1234);
            foreach (string key in new[] { "playback-session", "playback-session-backup" }) store.Set(key, JsonSerializer.Serialize(session));

            var result = TagEditing.Apply(track, new("Edited", "New artist", "New album", "", "Jazz", 2026, 2, 3), store.AcceptTagEdit);
            string scope = playlist ? "Playlist-only" : "Queue-only";
            check(store.TrackFingerprints()[track.Id] == result.Updated, scope + " tag edit refreshes source evidence");
            check(store.ReadAll(true).Count == 0, scope + " tag edit does not import the track into the library");
            foreach (string key in new[] { "playback-session", "playback-session-backup" })
            {
                var updated = PlaybackSession.Parse(store.Get(key)!);
                check(updated.Items[0].Track is { Title: "Edited", Artist: "New artist", Album: "New album", InLibrary: false }, scope + " metadata refreshes " + key);
                check(updated.Revision == 6 && updated.Position == 1234 && updated.CurrentItemId == item.ItemId && updated.Items[0].Track.Id == track.Id,
                    scope + " tag edit preserves queue identity and position in " + key);
                store.ValidateSource(updated.Items[0].Track);
            }
            if (playlist)
            {
                var snapshot = JsonSerializer.Deserialize<Dictionary<string, Track>>(store.Get("playlist-track-snapshots")!)![track.Id];
                check(snapshot.Title == "Edited" && snapshot.Metadata?.Disc == 2 && !snapshot.InLibrary, "Playlist snapshot receives edited tags without changing membership");
            }

            var stale = result.Updated with { Sha256 = new string('0', 64) };
            store.RememberSources([track], new Dictionary<string, SourceFingerprint> { [path] = stale });
            bool rejected = false;
            try { TagEditing.Apply(track, new("Rejected", "", "", "", "", 0, 0, 0), store.AcceptTagEdit); }
            catch (IOException) { rejected = true; }
            check(rejected && LocalFileIdentity.Capture(path) == result.Updated && MetadataReader.Read(path).Title == "Edited",
                scope + " stale source evidence rejects tag edits and restores the original file");
            check(PlaybackSession.Parse(store.Get("playback-session")!).Revision == 6, scope + " rejected edit leaves session unchanged");
        }
    }
}
