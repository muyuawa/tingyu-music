using System.Globalization;
using System.Text;

namespace Muyu.Core;

public enum CueExportPolicy { Unspecified, Skip, WholeSource }
public enum PlaylistPathStyle { Relative, Absolute }
public sealed record PlaylistExportRow(string Title, string SourcePath, string? OutputPath, double Duration, string Result, bool Warning);
public sealed record PlaylistExportPlan(IReadOnlyList<PlaylistExportRow> Rows, string? Error)
{
    public int Written => Rows.Count(r => r.OutputPath != null);
    public int Skipped => Rows.Count - Written;
    public int Warnings => Rows.Count(r => r.Warning);
    public bool CanWrite => Error == null && Written > 0;
}

public static class PlaylistExport
{
    private static (string Path, bool AbsoluteFallback) ExportPath(string source, PlaylistPathStyle style, string? destination)
    {
        string output = Path.GetFullPath(source); bool fallback = false;
        if (style == PlaylistPathStyle.Relative && destination != null)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(destination))!;
            fallback = !string.Equals(Path.GetPathRoot(folder), Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase);
            if (!fallback) output = Path.GetRelativePath(folder, output);
        }
        if (output.StartsWith('#')) output = "." + Path.DirectorySeparatorChar + output;
        return (output, fallback);
    }

    public static PlaylistExportPlan AtDestination(PlaylistExportPlan preview, PlaylistPathStyle style, string destination) => new(
        preview.Rows.Select(row => {
            if (row.OutputPath == null) return row;
            var path = ExportPath(row.SourcePath, style, destination);
            return row with { OutputPath = path.Path, Result = row.Result + (path.AbsoluteFallback ? "；跨卷，使用绝对路径" : ""), Warning = row.Warning || path.AbsoluteFallback };
        }).ToArray(), preview.Error);

    public static PlaylistExportPlan Preview(IEnumerable<Track> entries, CueExportPolicy cuePolicy,
        PlaylistPathStyle pathStyle, bool skipMissing, string? destination = null, Func<string, bool>? exists = null)
    {
        var tracks = entries.ToArray(); exists ??= File.Exists;
        var rows = new List<PlaylistExportRow>();
        if (tracks.Any(t => t.CuePath != null) && cuePolicy == CueExportPolicy.Unspecified)
            return new([], "包含 CUE 分轨，请先选择跳过分轨或每个整盘音源仅保留一次");
        var wholeSources = tracks.Where(t => t.CuePath != null).Select(t => t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            var row = new PlaylistExportRow(track.Title, track.Path, null, track.Duration, "", false);
            if (Uri.TryCreate(track.Path, UriKind.Absolute, out var uri) && !uri.IsFile)
            { rows.Add(row with { Result = "在线地址不支持，已跳过", Warning = true }); continue; }
            if (track.Path.IndexOfAny(['\r', '\n', '\0']) >= 0 || !Path.IsPathFullyQualified(track.Path))
            { rows.Add(row with { Result = "路径无效，已跳过", Warning = true }); continue; }
            if (track.CuePath != null && cuePolicy == CueExportPolicy.Skip)
            { rows.Add(row with { Result = "已跳过 CUE 分轨", Warning = true }); continue; }
            string fullPath = Path.GetFullPath(track.Path);
            bool whole = cuePolicy == CueExportPolicy.WholeSource && wholeSources.Contains(track.Path);
            if (whole && !seen.Add(fullPath))
            { rows.Add(row with { Result = "整盘音源已保留，合并此引用", Warning = true }); continue; }
            bool missing = !exists(fullPath);
            if (missing && skipMissing)
            { rows.Add(row with { Result = "文件缺失，已跳过", Warning = true }); continue; }
            var (output, absoluteFallback) = ExportPath(fullPath, pathStyle, destination);
            rows.Add(row with {
                Title = whole ? Path.GetFileNameWithoutExtension(track.Path) : track.Title,
                Duration = whole ? (double)track.TotalFrames / track.SampleRate : track.Duration,
                OutputPath = output,
                Result = string.Join("；", new[] { whole ? "整盘音源，不保留分轨范围" : "保留", missing ? "文件缺失，保留路径" : "", absoluteFallback ? "跨卷，使用绝对路径" : "" }.Where(s => s.Length > 0)),
                Warning = whole || missing || absoluteFallback
            });
        }
        return new(rows.AsReadOnly(), rows.Any(r => r.OutputPath != null) ? null : "没有可导出的曲目");
    }

    public static string Render(PlaylistExportPlan plan)
    {
        if (!plan.CanWrite) throw new InvalidOperationException(plan.Error ?? "没有可导出的曲目");
        var output = new StringBuilder("#EXTM3U\n");
        foreach (var row in plan.Rows.Where(r => r.OutputPath != null))
        {
            if (row.OutputPath!.IndexOfAny(['\r', '\n', '\0']) >= 0) throw new FormatException("导出路径包含无效字符");
            string title = row.Title.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            string duration = double.IsFinite(row.Duration) && row.Duration >= 0 ? row.Duration.ToString("0.###", CultureInfo.InvariantCulture) : "-1";
            output.Append("#EXTINF:").Append(duration).Append(',').Append(title).Append('\n').Append(row.OutputPath).Append('\n');
        }
        return output.ToString();
    }

    public static void Write(string destination, PlaylistExportPlan plan)
    {
        string path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(path), ".m3u8", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("请选择 .m3u8 文件");
        if (plan.Rows.Where(r => r.OutputPath != null).Any(r => string.Equals(Path.GetFullPath(r.SourcePath), path, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("不能用歌单覆盖源文件");
        string content = Render(plan);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, ".muyu-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false, true).GetBytes(content); stream.Write(bytes); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

public static class PlaylistSelection
{
    public static Track[] FromQueue(IReadOnlyList<QueueItem> queue, int current, bool includeHistory) =>
        queue.Skip(includeHistory ? 0 : Math.Max(0, current)).Select(q => q.Track).ToArray();
    public static int Duplicates(IEnumerable<Track> tracks) => tracks.GroupBy(t => t.Id).Sum(g => g.Count() - 1);
    public static string ValidateName(string name, IEnumerable<string> existing, string? replacing = null)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 120 || name.Any(char.IsControl)) throw new ArgumentException("名称须为 1 至 120 个字符，不能包含换行或控制字符");
        if (existing.Any(n => n != replacing && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已存在同名播放列表");
        return name;
    }
}
