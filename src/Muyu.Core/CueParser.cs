using System.Text;
using System.Text.RegularExpressions;

namespace Muyu.Core;

public static partial class CueParser
{
    [GeneratedRegex("^FILE\\s+(?:\"([^\"]+)\"|(\\S+))\\s+\\S+", RegexOptions.IgnoreCase)] private static partial Regex FileLine();
    [GeneratedRegex(@"^TRACK\s+(\d+)\s+AUDIO$",RegexOptions.IgnoreCase)] private static partial Regex TrackLine();
    [GeneratedRegex(@"^INDEX\s+(00|01)\s+(\d+):(\d{2}):(\d{2})$",RegexOptions.IgnoreCase)] private static partial Regex IndexLine();
    private sealed class Entry {public required string Path; public int Number; public string Title="",Artist=""; public long CdFrames=-1,Zero=-1;}
    public static IReadOnlyList<Track> Load(string path,Func<string,AudioInfo> probe,Encoding? encoding=null,bool pregapToNext=false,bool includeHidden=false,string? embeddedText=null,string? embeddedAudioPath=null)
    {
        string text=embeddedText??(encoding??new UTF8Encoding(false,true)).GetString(File.ReadAllBytes(path));
        if(embeddedAudioPath!=null&&text.Split('\n').Count(l=>FileLine().IsMatch(l.Trim().TrimStart('\uFEFF')))>1)throw new FormatException("内嵌 CUE 不能重定向多个 FILE 到同一音源");
        var entries=new List<Entry>(); string? currentPath=embeddedAudioPath; string album="",artist=""; Entry? current=null;
        foreach(string raw in text.Split('\n')) {
            string line=raw.Trim().TrimStart('\uFEFF'); var file=FileLine().Match(line);var track=TrackLine().Match(line);var index=IndexLine().Match(line);
            if(file.Success) {currentPath=embeddedAudioPath??System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!,file.Groups[1].Success?file.Groups[1].Value:file.Groups[2].Value));current=null;}
            else if(track.Success) {if(currentPath==null)throw new FormatException("CUE 曲目没有 FILE 来源");current=new Entry{Path=currentPath,Number=int.Parse(track.Groups[1].Value)};entries.Add(current);}
            else if(index.Success) {if(current==null)throw new FormatException("CUE 索引没有曲目");int second=int.Parse(index.Groups[3].Value),frame=int.Parse(index.Groups[4].Value);if(second>=60||frame>=75)throw new FormatException("CUE 时间范围无效");long value=(long.Parse(index.Groups[2].Value)*60+second)*75+frame;if(index.Groups[1].Value=="00"){if(current.Zero>=0)throw new FormatException("重复 INDEX 00");current.Zero=value;}else{if(current.CdFrames>=0)throw new FormatException("重复 INDEX 01");current.CdFrames=value;}}
            else if(line.StartsWith("TITLE ",StringComparison.OrdinalIgnoreCase)) {string value=line[6..].Trim().Trim('"');if(current==null)album=value;else current.Title=value;}
            else if(line.StartsWith("PERFORMER ",StringComparison.OrdinalIgnoreCase)) {string value=line[10..].Trim().Trim('"');if(current==null)artist=value;else current.Artist=value;}
        }
        if(entries.Count==0||entries.Any(e=>e.CdFrames<0))throw new FormatException("CUE 缺少有效音轨或 INDEX 01");
        if(entries.Any(e=>e.Zero>e.CdFrames)||entries.Select(e=>e.Number).Distinct().Count()!=entries.Count)throw new FormatException("CUE 轨号或 INDEX 00 顺序无效");
        var result=new List<Track>();
        foreach(var group in entries.GroupBy(e=>e.Path,StringComparer.OrdinalIgnoreCase)) {
            var source=probe(group.Key); var list=group.ToList(); long previous=-1;
            if(includeHidden&&list[0].CdFrames>0){long hiddenEnd=checked((pregapToNext&&list[0].Zero>=0?list[0].Zero:list[0].CdFrames)*source.SampleRate/75);if(hiddenEnd>0)result.Add(new(Track.Identity(group.Key,0,hiddenEnd),group.Key,"隐藏音轨 00",artist,album,source.SampleRate,source.Bits,source.Channels,source.Frames,0,hiddenEnd,path,Encoding:source.Encoding));}
            for(int i=0;i<list.Count;i++) {
                var e=list[i];long start=checked((pregapToNext&&e.Zero>=0?e.Zero:e.CdFrames)*source.SampleRate/75);long end=i+1<list.Count?checked((pregapToNext&&list[i+1].Zero>=0?list[i+1].Zero:list[i+1].CdFrames)*source.SampleRate/75):source.Frames;
                if(start<=previous||start>=end||end>source.Frames)throw new FormatException("CUE 分轨不是递增的有效样本范围");previous=start;
                result.Add(new(Track.Identity(group.Key,start,end),group.Key,string.IsNullOrWhiteSpace(e.Title)?$"音轨 {e.Number:00}":e.Title,string.IsNullOrWhiteSpace(e.Artist)?artist:e.Artist,album,source.SampleRate,source.Bits,source.Channels,source.Frames,start,end,path,Metadata:new TrackMetadata([string.IsNullOrWhiteSpace(e.Artist)?artist:e.Artist],[artist],[],0,1,1,(uint)e.Number,(uint)entries.Count),Encoding:source.Encoding));
            }
        }
        return result;
    }
}
