using System.Text.Json;
namespace Muyu.Core;
public sealed partial class LibraryStore
{
    public void AcceptTagEdit(TagEditResult result)
    {
        lock(writeGate){using var db=Open();using var tx=db.BeginTransaction();var tracks=Tracks(db,true,tx).Where(t=>t.Path.Equals(result.Path,StringComparison.OrdinalIgnoreCase)).ToArray();var changed=new Dictionary<string,Track>();
            foreach(var track in tracks){string? evidence=ScalarEvidence(track.Id);if(evidence!=null&&JsonSerializer.Deserialize<SourceFingerprint>(evidence)!=result.Original)throw new IOException("库中音源证据已变化，标签写入已回滚");var next=track with{Title=track.CuePath==null?result.Metadata.Title:track.Title,Artist=track.CuePath==null?result.Metadata.Artist:track.Artist,Album=track.CuePath==null?result.Metadata.Album:track.Album,Metadata=result.Metadata.Details};changed[track.Id]=next;Execute(db,"UPDATE tracks SET title=$title,artist=$artist,album=$album WHERE id=$id;INSERT INTO track_metadata VALUES($id,$metadata) ON CONFLICT(track) DO UPDATE SET value=excluded.value;INSERT INTO track_fingerprints VALUES($id,$fingerprint) ON CONFLICT(track) DO UPDATE SET value=excluded.value",tx,("$id",track.Id),("$title",next.Title),("$artist",next.Artist),("$album",next.Album),("$metadata",JsonSerializer.Serialize(next.Metadata)),("$fingerprint",JsonSerializer.Serialize(result.Updated)));}
            Execute(db,"INSERT INTO source_fingerprints VALUES($path,$value) ON CONFLICT(path) DO UPDATE SET value=excluded.value",tx,("$path",result.Path),("$value",JsonSerializer.Serialize(result.Updated)));
            foreach(string key in new[]{"playback-session","playback-session-backup"})if(Get(db,key,tx) is {} json){var session=PlaybackSession.Parse(json);Set(db,tx,key,JsonSerializer.Serialize(session with{Revision=session.Revision+1,Items=session.Items.Select(i=>changed.TryGetValue(i.Track.Id,out var t)?i with{Track=t}:i).ToArray()}));}
            if(Get(db,"playlist-track-snapshots",tx) is {} saved){var snapshots=JsonSerializer.Deserialize<Dictionary<string,Track>>(saved)??[];foreach(var item in changed)snapshots[item.Key]=item.Value;Set(db,tx,"playlist-track-snapshots",JsonSerializer.Serialize(snapshots));}
            Set(db,tx,"last-tag-backup",JsonSerializer.Serialize(result));BumpLibraryRevision(db,tx);tx.Commit();
            string? ScalarEvidence(string id){using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="SELECT value FROM track_fingerprints WHERE track=$id";command.Parameters.AddWithValue("$id",id);return command.ExecuteScalar() as string;}
        }
    }
}
