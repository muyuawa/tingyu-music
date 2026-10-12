namespace Muyu.Core;

public static class TrackTimeSort
{
    // Read each physical file once; CUE segments share its modification time.
    public static IReadOnlyList<Track> ByModified(IEnumerable<Track> source, bool newestFirst)
    {
        var tracks=source.ToArray();
        var times=new Dictionary<string,DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in tracks.Select(t=>t.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try{var file=new FileInfo(path);times[path]=file.Exists?new DateTimeOffset(file.LastWriteTimeUtc):null;}
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException){times[path]=null;}
        }
        return Order(tracks,t=>times[t.Path],newestFirst);
    }

    public static IReadOnlyList<Track> ByAdded(IEnumerable<Track> tracks,IReadOnlyDictionary<string,TrackUsage> usage,bool newestFirst)
        =>Order(tracks,t=>usage.TryGetValue(t.Id,out var value)?value.Added:null,newestFirst);

    private static IReadOnlyList<Track> Order(IEnumerable<Track> tracks,Func<Track,DateTimeOffset?> time,bool newestFirst)
        =>tracks.Select(t=>(Track:t,Time:time(t)))
            .OrderBy(t=>t.Time.HasValue?0:1)
            .ThenBy(t=>newestFirst?-(t.Time?.UtcTicks??0):(t.Time?.UtcTicks??0))
            .ThenBy(t=>t.Track.Disc).ThenBy(t=>t.Track.Number).ThenBy(t=>t.Track.StartFrame)
            .ThenBy(t=>t.Track.Title,StringComparer.CurrentCulture).ThenBy(t=>t.Track.Id,StringComparer.Ordinal)
            .Select(t=>t.Track).ToArray();
}
