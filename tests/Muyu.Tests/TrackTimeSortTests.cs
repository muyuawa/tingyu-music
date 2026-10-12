using Muyu.Core;

internal static class TrackTimeSortTests
{
    public static void Run(string fixtures,Action<bool,string> check)
    {
        string area=Path.Combine(fixtures,"time-sort-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        string oldFile=Path.Combine(area,"old.wav"),newFile=Path.Combine(area,"new.wav");
        File.WriteAllText(oldFile,"fixture");File.WriteAllText(newFile,"fixture");
        File.SetLastWriteTimeUtc(oldFile,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newFile,new DateTime(2024,1,1,0,0,0,DateTimeKind.Utc));
        Track Make(string id,string path,long start=0)=>new(id,path,id,"Artist","Album",44100,16,2,441000,start);
        var old=Make("old",oldFile);var next=Make("new",newFile);var missing=Make("missing",Path.Combine(area,"missing.wav"));
        Track[] tracks=[missing,old,next];
        check(TrackTimeSort.ByModified(tracks,true).Select(t=>t.Id).SequenceEqual(new[]{"new","old","missing"}),"mtime newest first and missing last");
        check(TrackTimeSort.ByModified(tracks,false).Select(t=>t.Id).SequenceEqual(new[]{"old","new","missing"}),"mtime oldest first and missing last");
        var first=Make("cue-first",oldFile);var second=Make("cue-second",oldFile,10000);
        check(TrackTimeSort.ByModified([second,first],true).SequenceEqual(new[]{first,second})&&TrackTimeSort.ByModified([second,first],false).SequenceEqual(new[]{first,second}),"CUE file timestamp ties preserve segment order");
        var usage=new Dictionary<string,TrackUsage>{[old.Id]=new(new DateTimeOffset(2025,1,1,0,0,0,TimeSpan.Zero),null,0),[next.Id]=new(new DateTimeOffset(2023,1,1,0,0,0,TimeSpan.Zero),null,0)};
        check(TrackTimeSort.ByAdded(tracks,usage,true).Select(t=>t.Id).SequenceEqual(new[]{"old","new","missing"}),"added time independent of file mtime, unknown last");
        check(TrackTimeSort.ByAdded(tracks,usage,false).Select(t=>t.Id).SequenceEqual(new[]{"new","old","missing"}),"added oldest first, unknown last");
        var db=new LibraryStore(Path.Combine(area,"data"));db.AddBatch([old]);var added=db.Usage()[old.Id].Added;
        db.AddBatch([old with{Title="Updated title"}]);db.RecordPlay(old.Id);
        check(new LibraryStore(Path.Combine(area,"data")).Usage()[old.Id].Added==added,"rescan and play preserve persisted first addition time");
        db.Set("library-sort","8");check(new LibraryStore(Path.Combine(area,"data")).Get("library-sort")=="8","sort preference persists across store reopen");
        File.SetLastWriteTimeUtc(oldFile,new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc));
        check(TrackTimeSort.ByModified(tracks,true)[0].Id==old.Id,"mtime refreshed after file changes");
    }
}
