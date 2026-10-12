using System.Text.Json;

namespace Muyu.Core;

public sealed partial class LibraryStore
{
    private sealed record UndoMapping(PathMapping[] Mappings, Track[] ExpectedTracks, Dictionary<string, SourceFingerprint> OriginalEvidence);
    public void ApplyRelocation(RelocationPlan plan)
    {
        lock (writeGate)
        {
            if (plan.Mappings.Length == 0 || plan.Mappings.Select(m => m.OldPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Mappings.Length ||
                plan.Mappings.Select(m => m.NewPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Mappings.Length)
                throw new InvalidOperationException("路径映射必须唯一且非空");
            var evidence = Fingerprints(plan.ExpectedTracks);
            foreach (var mapping in plan.Mappings)
            {
                if (!evidence.TryGetValue(mapping.OldPath, out var original) || original.Sha256 != mapping.Fingerprint.Sha256 || original.Length != mapping.Fingerprint.Length ||
                    LibraryScope.HasLink(mapping.NewPath) || LocalFileIdentity.Capture(mapping.NewPath) != mapping.Fingerprint)
                    throw new IOException("来源证据已变化，未应用路径映射");
            }
            using var db = Open(); using var tx = db.BeginTransaction();
            var current = Tracks(db, true, tx);
            if (plan.ExpectedTracks.Any(old => current.FirstOrDefault(t => t.Id == old.Id) != old)) throw new InvalidOperationException("音乐库在预览后已变化，请重新预览");
            foreach (var map in plan.Mappings)
            {
                if (current.Where(t => string.Equals(t.Path, map.OldPath, StringComparison.OrdinalIgnoreCase)).Any(t => !plan.ExpectedTracks.Contains(t))) throw new InvalidOperationException("来源曲目已变化");
                if (current.Any(t => string.Equals(t.Path, map.NewPath, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Path, map.OldPath, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("目标已属于其他库索引，未自动合并");
            }
            var paths = plan.Mappings.ToDictionary(m => m.OldPath, m => m.NewPath, StringComparer.OrdinalIgnoreCase);
            foreach (var map in plan.Mappings)
            {
                Execute(db, "UPDATE tracks SET path=$new WHERE path=$old COLLATE NOCASE", tx, ("$new", map.NewPath), ("$old", map.OldPath));
                Execute(db, "INSERT INTO source_fingerprints VALUES($path,$value) ON CONFLICT(path) DO UPDATE SET value=excluded.value", tx, ("$path", map.NewPath), ("$value", JsonSerializer.Serialize(map.Fingerprint)));
                foreach (var track in plan.ExpectedTracks.Where(t => string.Equals(t.Path, map.OldPath, StringComparison.OrdinalIgnoreCase)))
                    Execute(db, "UPDATE track_fingerprints SET value=$value WHERE track=$track", tx, ("$value", JsonSerializer.Serialize(map.Fingerprint)), ("$track", track.Id));
            }
            RemapSavedReferences(db, tx, paths);
            Set(db, tx, "path-mapping-undo", JsonSerializer.Serialize(new UndoMapping(plan.Mappings, plan.ExpectedTracks.Select(t => PlaybackSession.RemapTrack(t, paths)).ToArray(),
                evidence.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase))));
            BumpLibraryRevision(db,tx);tx.Commit();
        }
    }
    public IReadOnlyDictionary<string, string> UndoRelocation()
    {
        lock (writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            var undo = JsonSerializer.Deserialize<UndoMapping>(Get(db, "path-mapping-undo", tx) ?? "null") ?? throw new InvalidOperationException("没有可撤销的路径映射");
            var current = Tracks(db, true, tx);
            if (undo.ExpectedTracks.Any(old => current.FirstOrDefault(t => t.Id == old.Id) != old)) throw new InvalidOperationException("音乐库已变化，不能撤销此映射");
            if (current.Any(t => undo.Mappings.Any(m => string.Equals(t.Path, m.NewPath, StringComparison.OrdinalIgnoreCase)) && !undo.ExpectedTracks.Contains(t))) throw new InvalidOperationException("目标路径已有新引用，不能覆盖新索引");
            var paths = undo.Mappings.ToDictionary(m => m.NewPath, m => m.OldPath, StringComparer.OrdinalIgnoreCase);
            foreach (var map in paths)
            {
                Execute(db, "UPDATE tracks SET path=$old WHERE path=$new COLLATE NOCASE", tx, ("$old", map.Value), ("$new", map.Key));
                // The old location may be missing. Preserve its original evidence instead of reading it during undo.
                if (undo.OriginalEvidence.TryGetValue(map.Value, out var original))
                    foreach (var track in undo.ExpectedTracks.Where(t => string.Equals(t.Path, map.Key, StringComparison.OrdinalIgnoreCase)))
                        Execute(db, "UPDATE track_fingerprints SET value=$value WHERE track=$track", tx, ("$value", JsonSerializer.Serialize(original)), ("$track", track.Id));
            }
            RemapSavedReferences(db, tx, paths); Set(db, tx, "path-mapping-undo", "null"); BumpLibraryRevision(db,tx);tx.Commit(); return paths;
        }
    }
    private static void RemapSavedReferences(Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx, IReadOnlyDictionary<string, string> paths)
    {
        foreach (string key in new[] { "playback-session", "playback-session-backup" })
        {
            string? session = Get(db, key, tx);
            if (session == null) continue;
            try { Set(db, tx, key, JsonSerializer.Serialize(PlaybackSession.Parse(session).Remap(paths))); }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                Set(db, tx, key + "-invalid-" + Guid.NewGuid().ToString("N"), session);
                Execute(db, "DELETE FROM settings WHERE key=$key", tx, ("$key", key));
            }
        }
        string? tracks = Get(db, "playlist-track-snapshots", tx);
        if (tracks != null)
        {
            var items = JsonSerializer.Deserialize<Dictionary<string, Track>>(tracks)!;
            Set(db, tx, "playlist-track-snapshots", JsonSerializer.Serialize(items.ToDictionary(i => i.Key, i => PlaybackSession.RemapTrack(i.Value, paths))));
        }
        string? legacyQueue = Get(db, "queue-v2", tx);
        if (legacyQueue != null)
        {
            var queue = JsonSerializer.Deserialize<QueueItem[]>(legacyQueue)!;
            Set(db, tx, "queue-v2", JsonSerializer.Serialize(queue.Select(i => i with { Track = PlaybackSession.RemapTrack(i.Track, paths) })));
        }
    }
}
