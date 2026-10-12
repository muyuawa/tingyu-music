using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Muyu.Core;

public enum LyricsTimeDomain { Track, Album }
public sealed record LyricsCandidate(string Source, string Encoding, string Digest, Lyrics Lyrics, IReadOnlyList<string> Warnings, string Identity = "",string Parser="lrc-v3");
public sealed record LyricsBinding(string Source, string Encoding, string Revision, LyricsTimeDomain Domain, Lyrics Snapshot, double Delay, string Identity = "")
{
    public static LyricsBinding Create(LyricsCandidate candidate, LyricsTimeDomain domain, double delay = 0)
    {
        string revision = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{candidate.Parser}|{candidate.Source.ToUpperInvariant()}|{candidate.Identity}|{candidate.Encoding}|{candidate.Digest}|{domain}")));
        return new(candidate.Source, candidate.Encoding, revision, domain, candidate.Lyrics, delay, candidate.Identity);
    }
}
public sealed record LyricsBindingHistory(LyricsBinding Current, IReadOnlyList<LyricsBinding> Previous)
{
    public LyricsBindingHistory Replace(LyricsBinding next) => new(next,
        new[] { Current }.Concat(Previous).DistinctBy(b => b.Revision).ToArray());
}

public static class LyricsSources
{
    public static readonly string[] Encodings = ["auto", "utf-8", "gb18030", "shift_jis", "utf-16", "utf-16BE"];
    static LyricsSources() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static LyricsCandidate FromFile(string path, string encoding = "auto",IReadOnlyList<LyricsExtension>? extensions=null)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 4 * 1024 * 1024) throw new FormatException("歌词文件超过 4 MiB，无法预览");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        string selected = encoding;
        if (selected == "auto") selected = bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? "utf-16" :
            bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? "utf-16BE" : "utf-8";
        var decoder = Encoding.GetEncoding(selected, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        string text;
        try { text = decoder.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw new FormatException("编码不匹配，请选择 UTF-8、GB18030 或 Shift-JIS 后重新预览"); }
        var extension=extensions?.FirstOrDefault(e=>e.Enabled&&e.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()));
        if(extension!=null)return new(Path.GetFullPath(path),selected,Convert.ToHexString(SHA256.HashData(bytes)),extension.Parse(text,path),[],FileIdentity(stream.SafeFileHandle),extension.Id+":"+extension.Revision);
        return Candidate(Path.GetFullPath(path), selected, bytes, text, FileIdentity(stream.SafeFileHandle));
    }

    public static LyricsCandidate FromEmbedded(string path, string text)
    {
        using var stream = File.OpenRead(path);
        return Candidate(Path.GetFullPath(path) + "#embedded", "utf-8", Encoding.UTF8.GetBytes(text), text, FileIdentity(stream.SafeFileHandle));
    }

    private static LyricsCandidate Candidate(string source, string encoding, byte[] bytes, string text, string identity)
    {
        if (text.Length > 4 * 1024 * 1024 || text.Count(c => c == '\n') > 20000)
            throw new FormatException("歌词内容过长，无法预览");
        var parsed = LrcParser.Parse(text, source);
        if (parsed.Lines.Count == 0) throw new FormatException("歌词没有可用内容或可靠时间");
        return new(source, encoding, Convert.ToHexString(SHA256.HashData(bytes)), parsed, LrcParser.Warnings(text), identity);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    private static string FileIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return "unavailable";
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException("无法读取歌词来源的文件身份");
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }
}

public static class LyricsTiming
{
    public static double Origin(Track track, LyricsTimeDomain domain) => domain == LyricsTimeDomain.Album && track.SampleRate > 0
        ? (double)track.StartFrame / track.SampleRate : 0;
    public static double DisplayTime(double rawTime, Track track, LyricsBinding binding, double globalDelay, double? trialDelay = null) =>
        rawTime - Origin(track, binding.Domain) + binding.Snapshot.FileOffset + globalDelay + (trialDelay ?? binding.Delay);
    public static bool CanSeek(double seconds, double duration) => double.IsFinite(seconds) && seconds >= 0 && seconds < duration;
}

public sealed class LyricCorrectionDraft(string trackId, LyricsBinding binding)
{
    public string TrackId { get; } = trackId;
    public string Revision { get; } = binding.Revision;
    public double SavedDelay { get; } = binding.Delay;
    public double Delay { get; set; } = binding.Delay;
    public bool Dirty => Delay != SavedDelay;
    public bool AppliesTo(string trackId, LyricsBinding? binding) => TrackId == trackId && binding?.Revision == Revision;
    public LyricsBinding Commit(LyricsBinding binding)
    {
        if (binding.Revision != Revision) throw new InvalidOperationException("歌词来源已变化，请重新打开校正");
        if (!double.IsFinite(Delay)) throw new ArgumentException("歌词校正值无效");
        return binding with { Delay = Delay };
    }
}
