using System.Text.Json;
namespace Muyu.Core;
public sealed partial class LibraryStore
{
    public void AcceptTagEdit(TagEditResult result)
    {
        lock(writeGate){using var db=Open();using var tx=db.BeginTransaction();var tracks=Tracks(db,true,tx).Where(t=>t.Path.Equals(result.Path,StringComparison.OrdinalIgnoreCase)).ToArray();var changed=new HashSet<string>();
            Track Update(Track track)
            {
                if(!track.Path.Equals(result.Path,StringComparison.OrdinalIgnoreCase))return track;
                if(changed.Add(track.Id)){
                    string? evidence=ScalarEvidence(track.Id);
                    if(evidence!=null&&JsonSerializer.Deserialize<SourceFingerprint>(evidence)!=result.Original)throw new IOException("库中音源证据已变化，标签写入已回滚");
                    Execute(db,"INSERT INTO track_fingerprints VALUES($id,$fingerprint) ON CONFLICT(track) DO UPDATE SET value=excluded.value",tx,("$id",track.Id),("$fingerprint",JsonSerializer.Serialize(result.Updated)));
                }
                return TagEditing.UpdateTrack(track,result.Metadata);
            }
            foreach(var track in tracks){var next=Update(track);Execute(db,"UPDATE tracks SET title=$title,artist=$artist,album=$album WHERE id=$id;INSERT INTO track_metadata VALUES($id,$metadata) ON CONFLICT(track) DO UPDATE SET value=excluded.value",tx,("$id",track.Id),("$title",next.Title),("$artist",next.Artist),("$album",next.Album),("$metadata",JsonSerializer.Serialize(next.Metadata)));}
            Execute(db,"INSERT INTO source_fingerprints VALUES($path,$value) ON CONFLICT(path) DO UPDATE SET value=excluded.value",tx,("$path",result.Path),("$value",JsonSerializer.Serialize(result.Updated)));
            foreach(string key in new[]{"playback-session","playback-session-backup"})if(Get(db,key,tx) is {} json){var session=PlaybackSession.Parse(json);Set(db,tx,key,JsonSerializer.Serialize(session with{Revision=session.Revision+1,Items=session.Items.Select(i=>i with{Track=Update(i.Track)}).ToArray()}));}
            if(Get(db,"playlist-track-snapshots",tx) is {} saved){var snapshots=JsonSerializer.Deserialize<Dictionary<string,Track>>(saved)??[];Set(db,tx,"playlist-track-snapshots",JsonSerializer.Serialize(snapshots.ToDictionary(i=>i.Key,i=>Update(i.Value))));}
            if(Get(db,"queue-v2",tx) is {} legacy){var items=JsonSerializer.Deserialize<QueueItem[]>(legacy)??[];Set(db,tx,"queue-v2",JsonSerializer.Serialize(items.Select(i=>i with{Track=Update(i.Track)})));}
            Set(db,tx,"last-tag-backup",JsonSerializer.Serialize(result));BumpLibraryRevision(db,tx);tx.Commit();
            string? ScalarEvidence(string id){using var command=db.CreateCommand();command.Transaction=tx;command.CommandText="SELECT value FROM track_fingerprints WHERE track=$id";command.Parameters.AddWithValue("$id",id);return command.ExecuteScalar() as string;}
        }
    }
}
