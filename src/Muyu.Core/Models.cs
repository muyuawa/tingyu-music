using System.Security.Cryptography;
using System.Text;

namespace Muyu.Core;

public enum AudioEncoding { IntegerPcm,FloatPcm,Lossy,Dsd }
public sealed record AudioInfo(int SampleRate, int Bits, int Channels, int ContainerBits, long Frames,AudioEncoding Encoding=AudioEncoding.IntegerPcm);
public sealed record Track(string Id, string Path, string Title, string Artist, string Album,
    int SampleRate, int Bits, int Channels, long TotalFrames, long StartFrame = 0, long EndFrame = -1, string? CuePath = null, bool InLibrary = true, TrackMetadata? Metadata = null,AudioEncoding Encoding=AudioEncoding.IntegerPcm)
{
    public string AlbumArtist => Metadata?.AlbumArtists is { Length: > 0 } artists ? string.Join(" / ",artists) : Artist;
    public string Genre => Metadata?.Genres is { Length: > 0 } genres ? string.Join(" / ",genres) : "未分类";
    public uint Disc => Metadata?.Disc ?? 0;
    public uint Number => Metadata?.Number ?? 0;
    public string DisplayTitle => InLibrary ? Title : Title + " · 已移出音乐库";
    public long Frames => (EndFrame < 0 ? TotalFrames : EndFrame) - StartFrame;
    public double Duration => SampleRate > 0 ? (double)Frames / SampleRate : 0;
    public string DurationText => TimeSpan.FromSeconds(Duration).ToString(Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public string FormatText => $"{System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant()} · {Bits}-bit / {SampleRate / 1000.0:0.###} kHz";
    public static string Identity(string path, long start = 0, long end = -1) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{System.IO.Path.GetFullPath(path).ToUpperInvariant()}|{start}|{end}")));
    public static string SourceIdentity(SourceFingerprint evidence,long start,long end)=>Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{evidence.Identity}|{evidence.Sha256}|{start}|{end}")));
}
public sealed record LyricWord(double Time,string Text,int Start,int Length);
public sealed record LyricLine(double Time, string Text,IReadOnlyList<LyricWord>? Words=null);
public sealed record Lyrics(IReadOnlyList<LyricLine> Lines, bool Timed, double FileOffset, string? Source, string? RawOffset = null)
{
    public static Lyrics Empty { get; } = new([], false, 0, null);
    public int ActiveIndex(double outputTime, double userOffset = 0)
    {
        if (!Timed) return -1;
        double time = outputTime - FileOffset - userOffset;
        int lo=0, hi=Lines.Count-1, result=-1;
        while (lo<=hi) {int mid=(lo+hi)/2; if(Lines[mid].Time<=time){result=mid;lo=mid+1;}else hi=mid-1;}
        return result;
    }
}
public sealed record QueueItem(Guid ItemId, Track Track);
public sealed class PlaybackQueue
{
    private readonly List<QueueItem> items=[];
    public IReadOnlyList<QueueItem> Items => items;
    public int Current { get; private set; } = -1;
    private int nextInsertion;
    public Track? CurrentTrack => Current>=0&&Current<items.Count ? items[Current].Track : null;
    public void Replace(IEnumerable<Track> tracks, int index=0) {items.Clear();items.AddRange(tracks.Select(t=>new QueueItem(Guid.NewGuid(),t)));Current=items.Count>0?Math.Clamp(index,0,items.Count-1):-1;nextInsertion=Current+1;}
    public void AddNext(Track track) {items.Insert(Math.Clamp(nextInsertion,0,items.Count),new(Guid.NewGuid(),track));nextInsertion++;}
    public Track? Next() {if(Current+1>=items.Count)return null;Current++;nextInsertion=Current+1;return CurrentTrack;}
    public Track? Previous() {if(Current<=0)return CurrentTrack;Current--;nextInsertion=Current+1;return CurrentTrack;}
    public bool Remove(Guid itemId) {int i=items.FindIndex(t=>t.ItemId==itemId);if(i<0||i==Current)return false;items.RemoveAt(i);if(i<Current)Current--;if(i<nextInsertion)nextInsertion--;return true;}
}
