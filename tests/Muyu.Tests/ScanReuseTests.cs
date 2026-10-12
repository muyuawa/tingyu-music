using Muyu.Core;
using System.Diagnostics;
using System.Text.Json;
internal static class ScanReuseTests
{
    public static void Run(string fixtures,Action<bool,string> check)
    {
        string area=Path.Combine(fixtures,"scan-reuse-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(area);
        for(int i=0;i<32;i++)File.Copy(Path.Combine(fixtures,"reference-16.wav"),Path.Combine(area,$"{i:00}.wav"));
        var timer=Stopwatch.StartNew();var cold=LibraryScanner.Scan([area],CancellationToken.None);double coldMs=timer.Elapsed.TotalMilliseconds;
        timer.Restart();var warm=LibraryScanner.Scan([area],CancellationToken.None,cachedFingerprints:cold.Fingerprints,knownTracks:cold.Tracks);double warmMs=timer.Elapsed.TotalMilliseconds;
        check(cold.Tracks.Count==32&&warm.ReusedFiles==32&&cold.Tracks.SequenceEqual(warm.Tracks),"Unchanged scan reuses every ordinary track with identical metadata");
        check(cold.Fingerprints!.All(p=>warm.Fingerprints![p.Key]==p.Value),"Warm scan preserves verified source identities and hashes");
        string changed=cold.Tracks[0].Path;File.SetLastWriteTimeUtc(changed,DateTime.UtcNow.AddMinutes(1));
        var updated=LibraryScanner.Scan([area],CancellationToken.None,cachedFingerprints:cold.Fingerprints,knownTracks:cold.Tracks);
        check(updated.ReusedFiles==31&&updated.Fingerprints![changed].Modified!=cold.Fingerprints![changed].Modified,"Modification invalidates only affected scan entry");
        string cue=Path.Combine(area,"test.cue");File.WriteAllText(cue,"FILE \"00.wav\" WAVE\n TRACK 01 AUDIO\n TITLE \"New cue title\"\n INDEX 01 00:00:00\n");
        var cued=LibraryScanner.Scan([area],CancellationToken.None,cachedFingerprints:warm.Fingerprints,knownTracks:warm.Tracks);
        check(cued.Tracks.Any(t=>t.CuePath==cue&&t.Title=="New cue title")&&!cued.Tracks.Any(t=>t.Path==Path.Combine(area,"00.wav")&&t.CuePath==null),"New CUE takes precedence over cached whole-file entry");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool stopped=false;
        try{LibraryScanner.Scan([area],cancelled.Token,cachedFingerprints:warm.Fingerprints,knownTracks:warm.Tracks);}catch(OperationCanceledException){stopped=true;}
        check(stopped,"Cached scan remains cancellable");
        File.WriteAllText(Path.Combine(fixtures,"SCAN_REUSE_BENCHMARK.json"),JsonSerializer.Serialize(new{files=32,cold_ms=coldMs,warm_ms=warmMs,reused=warm.ReusedFiles},new JsonSerializerOptions{WriteIndented=true}));
    }
}
