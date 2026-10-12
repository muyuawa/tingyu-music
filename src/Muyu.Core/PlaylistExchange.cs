using System.Text;
using System.Text.Json;
namespace Muyu.Core;
public sealed record PlaylistImport(IReadOnlyList<Track> Tracks,IReadOnlyList<string> Warnings,IReadOnlyDictionary<string,SourceFingerprint>? Fingerprints=null);
public sealed record MuyuPlaylistEntry(string Path,long Start,long End,string? Cue,string Title,string Artist,string Album,string? Sha256=null);
public sealed record MuyuPlaylistDocument(int Version,string Name,MuyuPlaylistEntry[] Entries);
public static class PlaylistExchange
{
    public static void Save(string path,string name,IEnumerable<Track> tracks,IReadOnlyDictionary<string,SourceFingerprint>? evidence=null)
    {
        string destination=Path.GetFullPath(path),temporary=Path.Combine(Path.GetDirectoryName(destination)!,".muyu-export-"+Guid.NewGuid().ToString("N"));
        try{File.WriteAllText(temporary,Export(name,tracks,evidence),new UTF8Encoding(false));File.Move(temporary,destination,true);}
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    public static string Export(string name,IEnumerable<Track> tracks,IReadOnlyDictionary<string,SourceFingerprint>? evidence=null)=>JsonSerializer.Serialize(new MuyuPlaylistDocument(1,name,tracks.Select(t=>new MuyuPlaylistEntry(t.Path,t.StartFrame,t.EndFrame,t.CuePath,t.Title,t.Artist,t.Album,evidence?.GetValueOrDefault(t.Path)?.Sha256)).ToArray()),new JsonSerializerOptions{WriteIndented=true});
    public static PlaylistImport Import(string path,Func<string,AudioInfo> probe,Encoding? encoding=null)
    {
        if(new FileInfo(path).Length>16*1024*1024)throw new FormatException("歌单超过 16 MB 上限");
        string text=(encoding??new UTF8Encoding(false,true)).GetString(File.ReadAllBytes(path)).TrimStart('\uFEFF');
        var tracks=new List<Track>();var warnings=new List<string>();var infos=new Dictionary<string,AudioInfo>(StringComparer.OrdinalIgnoreCase);var tags=new Dictionary<string,Metadata>(StringComparer.OrdinalIgnoreCase);var evidence=new Dictionary<string,SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
        string Resolve(string value){if(Uri.TryCreate(value,UriKind.Absolute,out var uri)&&!uri.IsFile)throw new FormatException("已跳过网络地址："+value);return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!,Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.IsFile?uri.LocalPath:value));}
        AudioInfo Info(string source){if(LibraryScope.HasLink(source))throw new IOException("已跳过链接来源");using var guard=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);var fingerprint=LocalFileIdentity.Capture(source);if(evidence.TryGetValue(source,out var previous)&&previous!=fingerprint)throw new IOException("歌单预览期间音源已变化");evidence[source]=fingerprint;if(!infos.TryGetValue(source,out var info))infos[source]=info=probe(source);return info;}
        Track Ordinary(string source){using var guard=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);var info=Info(source);if(!tags.TryGetValue(source,out var tag))tags[source]=tag=MetadataReader.Read(source);return new(Track.SourceIdentity(evidence[source],0,-1),source,tag.Title,tag.Artist,tag.Album,info.SampleRate,info.Bits,info.Channels,info.Frames,InLibrary:false,Metadata:tag.Details,Encoding:info.Encoding);}
        if(Path.GetExtension(path).Equals(".muyu-playlist",StringComparison.OrdinalIgnoreCase))
        {
            var doc=JsonSerializer.Deserialize<MuyuPlaylistDocument>(text)??throw new FormatException("歌单内容为空");if(doc.Version!=1||doc.Entries==null||doc.Entries.Length>100000||doc.Entries.Any(e=>e==null||string.IsNullOrWhiteSpace(e.Path)||e.Title==null||e.Artist==null||e.Album==null))throw new FormatException("歌单版本、曲目数或内容不支持");
            foreach(var entry in doc.Entries)try{string source=Resolve(entry.Path);var track=Ordinary(source);long end=entry.End<0?track.TotalFrames:entry.End;if(entry.Start<0||end<=entry.Start||end>track.TotalFrames)throw new FormatException("片段样本范围无效");if(entry.Sha256!=null&&LocalFileIdentity.Capture(source).Sha256!=entry.Sha256)throw new IOException("音源内容不符，未绑定旧片段");tracks.Add(track with{Id=Track.Identity(source,entry.Start,entry.End),StartFrame=entry.Start,EndFrame=entry.End,CuePath=entry.Cue==null?null:Resolve(entry.Cue),Title=entry.Title,Artist=entry.Artist,Album=entry.Album});}catch(Exception ex)when(ex is not OutOfMemoryException){warnings.Add($"{entry.Path}：{ex.Message}");}
        }
        else
        {
            foreach(string raw in text.Replace("\r","").Split('\n')){string line=raw.Trim();if(line.Length==0||line.StartsWith('#'))continue;try{string source=Resolve(line);if(Path.GetExtension(source).Equals(".cue",StringComparison.OrdinalIgnoreCase))tracks.AddRange(CueParser.Load(source,Info));else if(LibraryScanner.AudioExtensions.Contains(Path.GetExtension(source),StringComparer.OrdinalIgnoreCase))tracks.Add(Ordinary(source));else throw new FormatException("不支持的文件类型");}catch(Exception ex)when(ex is not OutOfMemoryException){warnings.Add($"{line}：{ex.Message}");}if(tracks.Count>100000)throw new FormatException("歌单超过十万条上限");}
        }
        tracks=tracks.Select(t=>t with{Id=Track.SourceIdentity(evidence[t.Path],t.StartFrame,t.EndFrame),InLibrary=false}).ToList();return new(tracks,warnings,evidence);
    }
}
