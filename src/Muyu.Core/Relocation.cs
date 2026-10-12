namespace Muyu.Core;

public sealed record RelocationCandidate(string OldPath, string? NewPath, bool Verified, string Reason, SourceFingerprint? Fingerprint = null, AudioInfo? Audio = null)
{
    public override string ToString() => $"{OldPath}\n→ {NewPath ?? "未找到候选"}\n{Reason}" + (Audio == null ? "" : $" · {Audio.SampleRate} Hz / {Audio.Bits}-bit · {(double)Audio.Frames / Audio.SampleRate:0.###} s");
}
public sealed record PathMapping(string OldPath, string NewPath, SourceFingerprint Fingerprint);
public sealed record RelocationPlan(PathMapping[] Mappings, Track[] ExpectedTracks);

public static class Relocation
{
    public static string Availability(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.Directory) ? "路径不是音频文件" : "文件可用"; }
        catch (UnauthorizedAccessException) { return "没有读取权限"; }
        catch (IOException)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).IsReady ? "文件已移动或删除" : "磁盘未连接"; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { return "磁盘或共享目录未连接"; }
        }
    }
    public static IReadOnlyList<RelocationCandidate> Find(IReadOnlyList<Track> tracks, IReadOnlyDictionary<string, SourceFingerprint> original,
        IEnumerable<string> paths, CancellationToken cancellation)
    {
        var candidates = new List<(string Path, SourceFingerprint Evidence, AudioInfo Audio)>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                if (LibraryScope.HasLink(path)) continue;
                var info = NativeAudio.Probe(path); candidates.Add((path, LocalFileIdentity.Capture(path, cancellation), info));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
        var result = new List<RelocationCandidate>();
        foreach (var group in tracks.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!original.TryGetValue(group.Key, out var source)) { result.Add(new(group.Key, null, false, "缺少原始文件指纹，不能确认同一音源；请重新导入")); continue; }
            int before = result.Count;
            foreach (var candidate in candidates)
            {
                bool identical = source.Length == candidate.Evidence.Length && source.Sha256 == candidate.Evidence.Sha256;
                bool valid = group.All(t => ValidRange(t, candidate.Audio));
                if (identical || candidates.Count == 1 || string.Equals(Path.GetFileName(group.Key), Path.GetFileName(candidate.Path), StringComparison.OrdinalIgnoreCase))
                    result.Add(new(group.Key, candidate.Path, identical && valid,
                        !identical ? "内容不同，不能沿用原曲身份或播放位置" : !valid ? "音频参数或 CUE 片段范围不匹配" : "完整内容摘要与音频参数一致", candidate.Evidence, candidate.Audio));
            }
            if (before == result.Count) result.Add(new(group.Key, null, false, "所选范围内未找到匹配来源"));
        }
        return result;
    }
    public static bool ValidRange(Track track, AudioInfo audio) => track.SampleRate == audio.SampleRate && track.Bits == audio.Bits && track.Channels == audio.Channels &&
        track.TotalFrames == audio.Frames && track.StartFrame >= 0 && CuePlaybackRun.End(track) > track.StartFrame && CuePlaybackRun.End(track) <= audio.Frames;
    public static RelocationPlan Prepare(IReadOnlyList<Track> allTracks, IReadOnlyList<RelocationCandidate> selection, CancellationToken cancellation)
    {
        if (selection.Count == 0 || selection.Any(c => !c.Verified || c.NewPath == null || c.Fingerprint == null) ||
            selection.Select(c => c.OldPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Count ||
            selection.Select(c => c.NewPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selection.Count)
            throw new InvalidOperationException("每个来源须选择唯一且通过验证的候选");
        var maps = new List<PathMapping>();
        foreach (var selected in selection)
        {
            cancellation.ThrowIfCancellationRequested();
            if (LibraryScope.HasLink(selected.NewPath!)) throw new IOException("候选路径已变为链接");
            var fresh = LocalFileIdentity.Capture(selected.NewPath!, cancellation);
            if (fresh != selected.Fingerprint) throw new IOException("候选文件在预览后已变化，请重新核对");
            if (!allTracks.Any(t => string.Equals(t.Path, selected.OldPath, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("原始索引已变化");
            maps.Add(new(selected.OldPath, selected.NewPath!, fresh));
        }
        var affected = maps.Select(m => m.OldPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cues = allTracks.Where(t => affected.Contains(t.Path) && t.CuePath != null).Select(t => t.CuePath!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = allTracks.Where(t => affected.Contains(t.Path) || t.CuePath != null && cues.Contains(t.CuePath)).ToArray();
        foreach (var source in expected.GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            string path = maps.FirstOrDefault(m => string.Equals(m.OldPath, source.Key, StringComparison.OrdinalIgnoreCase))?.NewPath ?? source.Key;
            var info = NativeAudio.Probe(path);
            if (source.Any(t => !ValidRange(t, info))) throw new IOException("相关 CUE 来源有无效片段，未应用任何映射");
        }
        return new(maps.ToArray(), expected);
    }
}
