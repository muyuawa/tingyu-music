using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Muyu.Core;

public sealed partial class LibraryStore
{
    private readonly string connectionString;
    private readonly object writeGate = new();
    public LibraryStore(string folder)
    {
        Directory.CreateDirectory(folder);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "library.db") }.ToString();
        using var db = Open();
        if (Convert.ToInt64(Scalar(db, "PRAGMA user_version")) < 4 && Scalar(db, "SELECT name FROM sqlite_master WHERE name='tracks'") != null)
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "library-before-v04-" + Guid.NewGuid().ToString("N") + ".db") }.ToString());
            backup.Open(); db.BackupDatabase(backup);
        }
        Execute(db, "PRAGMA journal_mode=WAL");
        using var tx = db.BeginTransaction();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS tracks(id TEXT PRIMARY KEY,path TEXT NOT NULL,title TEXT,artist TEXT,album TEXT,rate INTEGER,bits INTEGER,channels INTEGER,total INTEGER,start INTEGER,end INTEGER,cue TEXT);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS library_roots(id TEXT PRIMARY KEY,path TEXT NOT NULL COLLATE NOCASE UNIQUE,identity TEXT NOT NULL,recursive INTEGER NOT NULL,watch INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS track_roots(track TEXT NOT NULL,root TEXT NOT NULL,PRIMARY KEY(track,root));
            CREATE TABLE IF NOT EXISTS track_state(track TEXT PRIMARY KEY,active INTEGER NOT NULL,direct INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS source_fingerprints(path TEXT PRIMARY KEY COLLATE NOCASE,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS track_fingerprints(track TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS root_source_paths(track TEXT NOT NULL,root TEXT NOT NULL,path TEXT NOT NULL,PRIMARY KEY(track,root));
            CREATE TABLE IF NOT EXISTS track_metadata(track TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS track_usage(track TEXT PRIMARY KEY,added TEXT NOT NULL,played TEXT,plays INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS track_encoding(track TEXT PRIMARY KEY,encoding INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS tracks_path ON tracks(path COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS tracks_album ON tracks(album,artist);
            INSERT OR IGNORE INTO track_fingerprints SELECT t.id,f.value FROM tracks t JOIN source_fingerprints f ON t.path=f.path COLLATE NOCASE;
            PRAGMA user_version=4;
            """, tx);
        tx.Commit();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    private static object? Scalar(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    { using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; return command.ExecuteScalar(); }
    private static void Execute(SqliteConnection db, string sql, SqliteTransaction? tx = null, params (string Key, object Value)[] parameters)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var parameter in parameters) cmd.Parameters.AddWithValue(parameter.Key, parameter.Value);
        cmd.ExecuteNonQuery();
    }
    private static IReadOnlyList<Track> Tracks(SqliteConnection db, bool includeRemoved, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT t.id,path,title,artist,album,rate,bits,channels,total,start,end,cue,COALESCE(s.active,1),m.value,COALESCE(e.encoding,0) FROM tracks t LEFT JOIN track_state s ON s.track=t.id LEFT JOIN track_metadata m ON m.track=t.id LEFT JOIN track_encoding e ON e.track=t.id " +
            (includeRemoved ? "" : "WHERE COALESCE(s.active,1)=1 ") + "ORDER BY album,CASE WHEN cue IS NOT NULL THEN path ELSE title END COLLATE NOCASE,start,title";
        using var r = cmd.ExecuteReader(); var rows = new List<Track>();
        while (r.Read()) rows.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetInt32(6), r.GetInt32(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10), r.IsDBNull(11) ? null : r.GetString(11), r.GetInt32(12) != 0,r.IsDBNull(13)?null:JsonSerializer.Deserialize<TrackMetadata>(r.GetString(13)),(AudioEncoding)r.GetInt32(14)));
        return rows;
    }
    public IReadOnlyList<Track> ReadAll() => ReadAll(false);
    public IReadOnlyList<Track> ReadAll(bool includeRemoved) { using var db = Open(); return Tracks(db, includeRemoved); }
    public IReadOnlyDictionary<string, SourceFingerprint> Fingerprints()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT path,value FROM source_fingerprints";
        using var reader = cmd.ExecuteReader(); var result = new Dictionary<string, SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) result[reader.GetString(0)] = JsonSerializer.Deserialize<SourceFingerprint>(reader.GetString(1))!;
        return result;
    }
    public IReadOnlyDictionary<string, SourceFingerprint> TrackFingerprints()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT track,value FROM track_fingerprints";
        using var reader = cmd.ExecuteReader(); var result = new Dictionary<string, SourceFingerprint>();
        while (reader.Read()) result[reader.GetString(0)] = JsonSerializer.Deserialize<SourceFingerprint>(reader.GetString(1))!;
        return result;
    }
    public IReadOnlyDictionary<string, SourceFingerprint> Fingerprints(IEnumerable<Track> tracks)
    {
        var evidence = TrackFingerprints(); var result = new Dictionary<string, SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in tracks.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            var entries = group.Select(t => evidence.GetValueOrDefault(t.Id)).ToArray();
            if (entries.All(e => e != null) && entries.Select(e => (e!.Length, e.Sha256)).Distinct().Count() == 1) result[group.Key] = entries[0]!;
        }
        return result;
    }
    public void ValidateSource(Track track, CancellationToken cancellation = default)
    {
        var evidence = TrackFingerprints().GetValueOrDefault(track.Id);
        if (evidence == null) return; // Legacy indexes acquire evidence on their next explicit scan.
        var current = LocalFileIdentity.Capture(track.Path, cancellation);
        if (current.Identity != evidence.Identity || current.Length != evidence.Length || current.Sha256 != evidence.Sha256)
            throw new IOException("音源内容或文件身份已变化，请重新导入或重新定位；未沿用旧播放位置");
    }
    public IReadOnlyList<Track> AddBatch(IReadOnlyList<Track> tracks, string? rootId = null, IReadOnlyDictionary<string, SourceFingerprint>? fingerprints = null)
    {
        lock (writeGate)
        {
            var knownEvidence = fingerprints == null ? null : Fingerprints();
            var originalTrackEvidence = fingerprints == null ? null : TrackFingerprints();
            var trackEvidence = originalTrackEvidence?.ToDictionary(e => e.Key, e => e.Value);
            if (rootId != null)
            {
                var scope = Roots().FirstOrDefault(r => r.Id == rootId) ?? throw new InvalidOperationException("扫描目录已被移除");
                LibraryScope.ValidateDirectory(scope.Path);
                if (LocalFileIdentity.DirectoryIdentity(scope.Path) != scope.Identity || tracks.Any(t => !LibraryScope.Contains(scope.Path, t.Path, scope.Recursive)))
                    throw new InvalidOperationException("目录身份或扫描范围已变化，未提交过期批次");
            }
            using var db = Open(); using var tx = db.BeginTransaction();
            var previous = Tracks(db, true, tx); var resolved = new List<Track>();
            foreach (var item in tracks)
            {
                var track = item;
                if (fingerprints != null && fingerprints.TryGetValue(item.Path, out var evidence))
                {
                    var same = previous.Concat(resolved).FirstOrDefault(t => t.StartFrame == item.StartFrame && t.EndFrame == item.EndFrame &&
                        trackEvidence!.TryGetValue(t.Id, out var old) && old.Identity == evidence.Identity && old.Sha256 == evidence.Sha256);
                    if (same != null)
                    {
                        bool keepPath = false;
                        try { var canonical = LocalFileIdentity.Capture(same.Path, cached: evidence); keepPath = canonical.Identity == evidence.Identity && canonical.Sha256 == evidence.Sha256; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        track = item with { Id = same.Id, Path = keepPath ? same.Path : item.Path };
                    }
                    else if (previous.Any(t => originalTrackEvidence!.TryGetValue(t.Id, out var old) &&
                        (t.Id == item.Id || old.Identity == evidence.Identity) && (old.Identity != evidence.Identity || old.Sha256 != evidence.Sha256)))
                    {
                        track = item with { Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{evidence.Identity}|{evidence.Sha256}|{item.StartFrame}|{item.EndFrame}"))) };
                        var alias = previous.FirstOrDefault(t => t.StartFrame == item.StartFrame && t.EndFrame == item.EndFrame && originalTrackEvidence!.GetValueOrDefault(t.Id)?.Identity == evidence.Identity);
                        if (alias != null)
                        {
                            try { var canonical = LocalFileIdentity.Capture(alias.Path, cached: evidence); if (canonical.Identity == evidence.Identity && canonical.Sha256 == evidence.Sha256) track = track with { Path = alias.Path }; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                        }
                    }
                    trackEvidence![track.Id] = evidence;
                    Execute(db, "INSERT INTO track_fingerprints VALUES($track,$value) ON CONFLICT(track) DO UPDATE SET value=excluded.value", tx,
                        ("$track", track.Id), ("$value", JsonSerializer.Serialize(evidence)));
                    Execute(db, "INSERT INTO source_fingerprints VALUES($path,$value) ON CONFLICT(path) DO UPDATE SET value=excluded.value", tx,
                        ("$path", item.Path), ("$value", JsonSerializer.Serialize(evidence)));
                    if (!string.Equals(item.Path, track.Path, StringComparison.OrdinalIgnoreCase))
                        Execute(db, "INSERT INTO source_fingerprints VALUES($path,$value) ON CONFLICT(path) DO UPDATE SET value=excluded.value", tx,
                            ("$path", track.Path), ("$value", JsonSerializer.Serialize(evidence)));
                }
                resolved.Add(track with { InLibrary = true });
            }
            var replaced = fingerprints?.Where(p => knownEvidence!.TryGetValue(p.Key, out var old) && (old.Identity != p.Value.Identity || old.Sha256 != p.Value.Sha256)).Select(p => p.Key) ?? [];
            var changedAliases = fingerprints == null ? [] : previous.Where(t => originalTrackEvidence!.TryGetValue(t.Id, out var old) && fingerprints.Values.Any(f => f.Identity == old.Identity && f.Sha256 != old.Sha256)).Select(t => t.Path);
            foreach (var source in resolved.Where(t => t.CuePath != null).Select(t => t.Path).Concat(replaced).Concat(changedAliases).Distinct(StringComparer.OrdinalIgnoreCase))
                Execute(db, "INSERT INTO track_state SELECT id,0,0 FROM tracks WHERE path=$path COLLATE NOCASE ON CONFLICT(track) DO UPDATE SET active=0", tx, ("$path", source));
            var added = new List<Track>();
            for (int trackIndex = 0; trackIndex < resolved.Count; trackIndex++)
            {
                var t = resolved[trackIndex]; string sourcePath = tracks[trackIndex].Path;
                if (t.CuePath == null)
                {
                    using var existing = db.CreateCommand(); existing.Transaction = tx;
                    existing.CommandText = "SELECT t.id FROM tracks t LEFT JOIN track_state s ON s.track=t.id WHERE path=$path COLLATE NOCASE AND cue IS NOT NULL AND COALESCE(s.active,1)=1";
                    existing.Parameters.AddWithValue("$path", t.Path); var cueIds = new List<string>(); using (var reader = existing.ExecuteReader()) while (reader.Read()) cueIds.Add(reader.GetString(0));
                    if (cueIds.Count > 0)
                    {
                        if (rootId != null) foreach (var id in cueIds) AttachRoot(db, tx, id, rootId, sourcePath);
                        continue;
                    }
                }
                Execute(db, """
                    INSERT INTO tracks VALUES($id,$path,$title,$artist,$album,$rate,$bits,$channels,$total,$start,$end,$cue)
                    ON CONFLICT(id) DO UPDATE SET path=excluded.path,title=excluded.title,artist=excluded.artist,album=excluded.album,rate=excluded.rate,bits=excluded.bits,channels=excluded.channels,total=excluded.total,start=excluded.start,end=excluded.end,cue=excluded.cue
                    """, tx, ("$id", t.Id), ("$path", t.Path), ("$title", t.Title), ("$artist", t.Artist), ("$album", t.Album), ("$rate", t.SampleRate), ("$bits", t.Bits), ("$channels", t.Channels),
                    ("$total", t.TotalFrames), ("$start", t.StartFrame), ("$end", t.EndFrame), ("$cue", t.CuePath ?? (object)DBNull.Value));
                Execute(db, "INSERT INTO track_state VALUES($id,1,$direct) ON CONFLICT(track) DO UPDATE SET active=1,direct=MAX(direct,excluded.direct)", tx, ("$id", t.Id), ("$direct", rootId == null ? 1 : 0));
                if(t.Metadata!=null)Execute(db,"INSERT INTO track_metadata VALUES($id,$value) ON CONFLICT(track) DO UPDATE SET value=excluded.value",tx,("$id",t.Id),("$value",JsonSerializer.Serialize(t.Metadata)));
                Execute(db,"INSERT INTO track_encoding VALUES($id,$encoding) ON CONFLICT(track) DO UPDATE SET encoding=excluded.encoding",tx,("$id",t.Id),("$encoding",(int)t.Encoding));
                Execute(db,"INSERT OR IGNORE INTO track_usage(track,added,plays) VALUES($id,$added,0)",tx,("$id",t.Id),("$added",DateTimeOffset.UtcNow.ToString("O")));
                Execute(db, "INSERT OR IGNORE INTO track_roots SELECT $id,r.root FROM track_roots r JOIN tracks t ON t.id=r.track WHERE t.path=$path COLLATE NOCASE", tx, ("$id", t.Id), ("$path", t.Path));
                Execute(db, "INSERT OR IGNORE INTO root_source_paths SELECT $id,r.root,r.path FROM root_source_paths r JOIN tracks t ON t.id=r.track WHERE t.path=$path COLLATE NOCASE", tx, ("$id", t.Id), ("$path", t.Path));
                if (rootId != null) AttachRoot(db, tx, t.Id, rootId, sourcePath);
                added.Add(t);
            }
            BumpLibraryRevision(db,tx);tx.Commit(); return added;
        }
    }
    private static void AttachRoot(SqliteConnection db, SqliteTransaction tx, string track, string root, string path)
    {
        Execute(db, "INSERT OR IGNORE INTO track_roots VALUES($track,$root)", tx, ("$track", track), ("$root", root));
        Execute(db, "INSERT INTO root_source_paths VALUES($track,$root,$path) ON CONFLICT(track,root) DO UPDATE SET path=excluded.path", tx,
            ("$track", track), ("$root", root), ("$path", path));
    }
    public IReadOnlyList<LibraryRoot> Roots()
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,path,identity,recursive,watch FROM library_roots ORDER BY path";
        using var reader = cmd.ExecuteReader(); var result = new List<LibraryRoot>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetInt32(4) != 0));
        return result;
    }
    public LibraryRoot RegisterRoot(string path, bool recursive, bool watch)
    {
        path = LibraryScope.Normalize(path); LibraryScope.ValidateDirectory(path); string identity = LocalFileIdentity.DirectoryIdentity(path);
        lock (writeGate)
        {
            var roots = Roots(); var existing = roots.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null && existing.Identity != identity) throw new IOException("此路径的目录身份已变化，请先移除旧目录记录再添加");
            var root = new LibraryRoot(existing?.Id ?? Guid.NewGuid().ToString("N"), path, identity, recursive, watch);
            using var db = Open(); using var tx = db.BeginTransaction(); Execute(db, "INSERT INTO library_roots VALUES($id,$path,$identity,$recursive,$watch) ON CONFLICT(id) DO UPDATE SET recursive=excluded.recursive,watch=excluded.watch", tx,
                ("$id", root.Id), ("$path", root.Path), ("$identity", root.Identity), ("$recursive", recursive ? 1 : 0), ("$watch", watch ? 1 : 0));
            if (existing != null && existing.Recursive && !recursive)
            {
                using var cmd = db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "SELECT r.track,COALESCE(p.path,t.path) FROM track_roots r JOIN tracks t ON t.id=r.track LEFT JOIN root_source_paths p ON p.track=r.track AND p.root=r.root WHERE r.root=$root";
                cmd.Parameters.AddWithValue("$root", root.Id); var removed = new List<string>();
                using (var reader = cmd.ExecuteReader()) while (reader.Read()) if (!LibraryScope.Contains(path, reader.GetString(1), false)) removed.Add(reader.GetString(0));
                foreach (string id in removed) Execute(db, "DELETE FROM track_roots WHERE root=$root AND track=$track; DELETE FROM root_source_paths WHERE root=$root AND track=$track", tx, ("$root", root.Id), ("$track", id));
                Execute(db, "UPDATE track_state SET active=0 WHERE direct=0 AND track NOT IN (SELECT track FROM track_roots)", tx);
            }
            BumpLibraryRevision(db,tx);tx.Commit();
            return root;
        }
    }
    public int RootTrackCount(string rootId)
    {
        using var db = Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM track_roots r LEFT JOIN track_state s ON s.track=r.track WHERE root=$root AND COALESCE(s.active,1)=1"; cmd.Parameters.AddWithValue("$root", rootId); return Convert.ToInt32(cmd.ExecuteScalar());
    }
    public void RemoveRoot(string rootId)
    {
        lock (writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            Execute(db, "UPDATE track_state SET active=0 WHERE direct=0 AND track IN (SELECT track FROM track_roots WHERE root=$root) AND track NOT IN (SELECT track FROM track_roots WHERE root<>$root)", tx, ("$root", rootId));
            Execute(db, "DELETE FROM track_roots WHERE root=$root; DELETE FROM root_source_paths WHERE root=$root; DELETE FROM library_roots WHERE id=$root", tx, ("$root", rootId));
            BumpLibraryRevision(db,tx);tx.Commit();
        }
    }
    public string? Get(string key) { using var db = Open(); return Get(db, key); }
    private static string? Get(SqliteConnection db, string key, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = "SELECT value FROM settings WHERE key=$key"; cmd.Parameters.AddWithValue("$key", key); return cmd.ExecuteScalar() as string;
    }
    private static void Set(SqliteConnection db, SqliteTransaction tx, string key, string value) =>
        Execute(db, "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", tx, ("$key", key), ("$value", value));
    public void Set(string key, string value) => SetMany(new Dictionary<string, string> { [key] = value });
    public void SetMany(IReadOnlyDictionary<string, string> values)
    {
        lock (writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction();
            foreach (var value in values) Set(db, tx, value.Key, value.Value);
            if(values.ContainsKey("favorites"))BumpLibraryRevision(db,tx);
            tx.Commit();
        }
    }
    public void SaveSession(PlaybackSession session)
    {
        session = session.Validate();
        lock (writeGate)
        {
            using var db = Open(); using var tx = db.BeginTransaction(); string? old = Get(db, "playback-session", tx);
            PlaybackSession? previous = null;
            if (old != null) { try { previous = PlaybackSession.Parse(old); } catch (Exception ex) when (ex is JsonException or FormatException) { } }
            if (previous != null && previous.Revision >= session.Revision) throw new InvalidOperationException("会话已变化，未覆盖较新的播放位置");
            if (previous != null) Set(db, tx, "playback-session-backup", old!);
            else if (old != null) Set(db, tx, "playback-session-invalid-" + Guid.NewGuid().ToString("N"), old);
            Set(db, tx, "playback-session", JsonSerializer.Serialize(session)); tx.Commit();
        }
    }
}
