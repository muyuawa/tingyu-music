using System.Globalization;
using Microsoft.Data.Sqlite;
namespace Muyu.Core;
public sealed record TrackUsage(DateTimeOffset Added,DateTimeOffset? Played,int Plays);
public sealed partial class LibraryStore
{
    private static void BumpLibraryRevision(SqliteConnection db,SqliteTransaction tx)=>Execute(db,"INSERT INTO settings VALUES('library-revision','1') ON CONFLICT(key) DO UPDATE SET value=CAST(CAST(value AS INTEGER)+1 AS TEXT)",tx);
    public long LibraryRevision=>long.TryParse(Get("library-revision"),out var value)?value:0;
    public IReadOnlyDictionary<string,TrackUsage> Usage()
    {
        using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="SELECT track,added,played,plays FROM track_usage";using var reader=cmd.ExecuteReader();var result=new Dictionary<string,TrackUsage>();
        while(reader.Read())result[reader.GetString(0)]=new(DateTimeOffset.Parse(reader.GetString(1),CultureInfo.InvariantCulture),reader.IsDBNull(2)?null:DateTimeOffset.Parse(reader.GetString(2),CultureInfo.InvariantCulture),reader.GetInt32(3));return result;
    }
    public void RecordPlay(string id)
    {
        lock(writeGate){using var db=Open();using var tx=db.BeginTransaction();Execute(db,"INSERT INTO track_usage VALUES($id,$now,$now,1) ON CONFLICT(track) DO UPDATE SET played=excluded.played,plays=plays+1",tx,("$id",id),("$now",DateTimeOffset.UtcNow.ToString("O")));BumpLibraryRevision(db,tx);tx.Commit();}
    }
    public void RemoveFromLibrary(IEnumerable<string> ids)
    {
        lock(writeGate){using var db=Open();using var tx=db.BeginTransaction();foreach(var id in ids.Distinct())Execute(db,"INSERT INTO track_state VALUES($id,0,0) ON CONFLICT(track) DO UPDATE SET active=0,direct=0;DELETE FROM track_roots WHERE track=$id;DELETE FROM root_source_paths WHERE track=$id",tx,("$id",id));BumpLibraryRevision(db,tx);tx.Commit();}
    }
    public void RememberSources(IEnumerable<Track> tracks,IReadOnlyDictionary<string,SourceFingerprint> evidence)
    {
        lock(writeGate){using var db=Open();using var tx=db.BeginTransaction();foreach(var track in tracks.DistinctBy(t=>t.Id)){if(!evidence.TryGetValue(track.Path,out var fingerprint))throw new IOException("缺少来源证据");Execute(db,"INSERT INTO track_fingerprints VALUES($id,$value) ON CONFLICT(track) DO UPDATE SET value=excluded.value",tx,("$id",track.Id),("$value",System.Text.Json.JsonSerializer.Serialize(fingerprint)));}tx.Commit();}
    }
}
