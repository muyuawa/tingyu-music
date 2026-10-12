namespace Muyu.Core;

public sealed record ScanResult(IReadOnlyList<Track> Tracks, IReadOnlyList<string> Errors, IReadOnlyList<ScanIssue>? Issues = null, int CheckedFiles = 0,IReadOnlyDictionary<string,SourceFingerprint>? Fingerprints=null,int ReusedFiles=0);
public sealed class ScanInterruptedException(ScanResult partialResult, OperationCanceledException cause, CancellationToken cancellation)
    : OperationCanceledException("扫描已取消，已提交批次保留", cause, cancellation)
{
    public ScanResult PartialResult { get; } = partialResult;
}

public static class LibraryScanner
{
    public static ScanResult Scan(IEnumerable<string> paths, CancellationToken cancellation, IProgress<int>? progress = null, Action<IReadOnlyList<Track>>? commit = null, bool recursive = true,ScanControl? control=null,Action<IReadOnlyList<Track>,IReadOnlyDictionary<string,SourceFingerprint>>? verifiedCommit=null,IReadOnlyDictionary<string,SourceFingerprint>? cachedFingerprints=null,IReadOnlyList<Track>? knownTracks=null)
    {
        var inputs = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var issues = new List<ScanIssue>();
        var tracks = new List<Track>(); int checkedFiles = 0,reusedFiles=0;
        long lastProgress=0;
        void Report(bool force=false){long now=Environment.TickCount64;if(force||now-lastProgress>=100){lastProgress=now;progress?.Report(checkedFiles);}}
        var known=(knownTracks??[]).Where(t=>t.CuePath==null&&t.Metadata!=null).GroupBy(t=>t.Path,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.OrdinalIgnoreCase);
        var probeCache=new Dictionary<string,AudioInfo>(StringComparer.OrdinalIgnoreCase);
        var fingerprints=new Dictionary<string,SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
        ScanResult Result() => new(tracks.ToArray(), errors.Concat(issues.Select(i => $"{i.Path}：{i.Message}")).ToArray(), issues.ToArray(), checkedFiles,new Dictionary<string,SourceFingerprint>(fingerprints,StringComparer.OrdinalIgnoreCase),reusedFiles);
        AudioInfo Probe(string path){using var guard=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);if(probeCache.TryGetValue(path,out var cachedInfo))return cachedInfo;var evidence=LocalFileIdentity.Capture(path,cancellation,cachedFingerprints?.GetValueOrDefault(path));var info=NativeAudio.Probe(path);probeCache[path]=info;fingerprints[path]=evidence;return info;}
        void Commit(IReadOnlyList<Track> batch){verifiedCommit?.Invoke(batch,fingerprints);commit?.Invoke(batch);}
        var roots = inputs.Where(Directory.Exists).ToArray();
        try
        {
        foreach (string path in inputs)
        {
            cancellation.ThrowIfCancellationRequested();
            control?.Checkpoint(cancellation);
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (string file in LibraryScope.Enumerate(path, recursive, cancellation, issues))
                    {
                        control?.Checkpoint(cancellation);
                        checkedFiles++; Report();
                        if (Supported(file)) files.Add(file);
                    }
                }
                else if (Supported(path))
                {
                    if (LibraryScope.HasLink(path)) issues.Add(new(path, "link", "已跳过符号链接"));
                    else { files.Add(path); checkedFiles++; Report(); }
                }
                else issues.Add(new(path, "input", "目录离线或文件类型不支持"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(path, "input", ex.Message)); }
        }
        var cueSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string cue in files.Where(p => Path.GetExtension(p).Equals(".cue", StringComparison.OrdinalIgnoreCase)).Order())
        {
            control?.Checkpoint(cancellation);
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var entries = CueParser.Load(cue, path => {
                    bool inScope = roots.Any(root => LibraryScope.Contains(root, path, recursive)) || files.Contains(path) ||
                        inputs.Contains(cue, StringComparer.OrdinalIgnoreCase) && LibraryScope.Contains(Path.GetDirectoryName(cue)!, path, false);
                    if (!inScope || LibraryScope.HasLink(path)) throw new IOException("CUE 引用超出所选范围或经过链接，未读取外部音源");
                    return Probe(path);
                });
                if (entries.Any(t => cueSources.Contains(t.Path))) { issues.Add(new(cue, "cue-conflict", "同一音源已有 CUE，保留先导入的方案")); continue; }
                tracks.AddRange(entries); foreach (var track in entries) cueSources.Add(track.Path);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { issues.Add(new(cue, "cue", ex.Message)); }
        }
        if (tracks.Count > 0) Commit(tracks.ToArray());
        var batch = new List<Track>();
        foreach (string path in files.Where(p => !Path.GetExtension(p).Equals(".cue", StringComparison.OrdinalIgnoreCase)))
        {
            control?.Checkpoint(cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!cueSources.Contains(path))
            {
                try
                {
                    using var guard=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                    var cached=cachedFingerprints?.GetValueOrDefault(path);
                    var evidence=LocalFileIdentity.Capture(path,cancellation,cached);fingerprints[path]=evidence;
                    if(cached!=null&&evidence==cached&&known.TryGetValue(path,out var existing))
                    {
                        tracks.Add(existing);batch.Add(existing);reusedFiles++;
                        if(batch.Count>=32){Commit(batch.ToArray());batch.Clear();}
                        Report();continue;
                    }
                    probeCache.Remove(path);
                    var info=NativeAudio.Probe(path);probeCache[path]=info;
                    var tag = MetadataReader.Read(path,includeCover:false);
                    if(!string.IsNullOrWhiteSpace(tag.EmbeddedCue)){
                        try{var embedded=CueParser.Load(path,Probe,embeddedText:tag.EmbeddedCue,embeddedAudioPath:path);tracks.AddRange(embedded);batch.AddRange(embedded);cueSources.Add(path);if(batch.Count>=32){Commit(batch.ToArray());batch.Clear();}continue;}
                        catch(Exception ex)when(ex is not OperationCanceledException){issues.Add(new(path,"embedded-cue",ex.Message+"；保留整轨"));}
                    }
                    var track = new Track(Track.Identity(path), path, tag.Title, tag.Artist, tag.Album, info.SampleRate, info.Bits, info.Channels, info.Frames,Metadata:tag.Details,Encoding:info.Encoding);
                    tracks.Add(track); batch.Add(track);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { issues.Add(new(path, "audio", ex.Message)); }
            }
            Report();
            if (batch.Count >= 32) { Commit(batch.ToArray()); batch.Clear(); }
        }
        if (batch.Count > 0) Commit(batch);
        Report(true);return Result();
        }
        catch (OperationCanceledException ex) { throw new ScanInterruptedException(Result(), ex, cancellation); }
    }
    public static readonly string[] AudioExtensions = [".flac", ".wav", ".mp3", ".m4a", ".aac", ".alac", ".aif", ".aiff", ".ape", ".wv", ".ogg", ".opus", ".dsf", ".dff"];
    public static bool Supported(string path) => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase) || AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
