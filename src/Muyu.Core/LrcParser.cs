using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Muyu.Core;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d+):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex Timestamp();
    [GeneratedRegex(@"\[offset\s*:\s*([+-]?\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
    [GeneratedRegex(@"^\[[a-zA-Z]+:.*\]$")]
    private static partial Regex Metadata();
    [GeneratedRegex(@"<(\d+):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTimestamp();
    public static IReadOnlyList<string> Warnings(string text)
    {
        var warnings = new List<string>();
        if (text.Contains('\uFFFD')) warnings.Add("内容含替换字符，请核对编码");
        foreach(string row in text.Split('\n')){double last=-1;foreach(Match word in WordTimestamp().Matches(row)){if(!int.TryParse(word.Groups[1].Value,out int m)||!int.TryParse(word.Groups[2].Value,out int s)||s>=60){warnings.Add("存在无效逐字时间，该行使用行级定位");continue;}string f=word.Groups[3].Value;double t=m*60.0+s+(f.Length==0?0:int.Parse(f,CultureInfo.InvariantCulture)/Math.Pow(10,f.Length));if(t<last)warnings.Add("逐字时间不递增，该行使用行级高亮");last=t;}}
        double previous = -1;
        foreach (Match match in Timestamp().Matches(text))
        {
            if (!int.TryParse(match.Groups[1].Value, out int minutes) || !int.TryParse(match.Groups[2].Value, out int seconds) || seconds >= 60)
            { warnings.Add("存在无效时间，该时间不会用于定位"); continue; }
            double value = minutes * 60.0 + seconds;
            if (value < previous) warnings.Add("原始行时间不是递增顺序，预览已按时间排序");
            previous = value;
        }
        return warnings.Distinct().ToArray();
    }
    public static Lyrics Parse(string text, string? source=null)
    {
        double offset=0;
        var offsetMatch=Offset().Match(text);
        // Normalize this parser's file-offset dialect: positive raw offset moves lyrics earlier.
        if(offsetMatch.Success&&double.TryParse(offsetMatch.Groups[1].Value,CultureInfo.InvariantCulture,out var ms))offset=-ms/1000;
        var timed=new List<LyricLine>(); var plain=new List<LyricLine>();
        foreach(string raw in text.Replace("\r","").Split('\n')) {
            string line=raw.Trim().TrimStart('\uFEFF'); if(line.Length==0)continue;
            var stamps=Timestamp().Matches(line);
            if(stamps.Count==0){if(!Metadata().IsMatch(line))plain.Add(new(0,line));continue;}
            string withWords=Timestamp().Replace(line,"").Trim();var wordStamps=WordTimestamp().Matches(withWords);var words=new List<LyricWord>();var contentBuilder=new StringBuilder();int endOfWord=0;
            for(int wi=0;wi<wordStamps.Count;wi++){var stamp=wordStamps[wi];contentBuilder.Append(withWords[endOfWord..stamp.Index]);int wordEnd=wi+1<wordStamps.Count?wordStamps[wi+1].Index:withWords.Length;string word=withWords[(stamp.Index+stamp.Length)..wordEnd];if(int.TryParse(stamp.Groups[1].Value,out int wm)&&int.TryParse(stamp.Groups[2].Value,out int ws)&&ws<60){string fraction=stamp.Groups[3].Value;double wt=wm*60.0+ws+(fraction.Length==0?0:int.Parse(fraction,CultureInfo.InvariantCulture)/Math.Pow(10,fraction.Length));if(word.Length>0)words.Add(new(wt,word,contentBuilder.Length,word.Length));}contentBuilder.Append(word);endOfWord=wordEnd;}
            if(wordStamps.Count==0)contentBuilder.Append(withWords);string content=contentBuilder.ToString();
            double? firstTime=null;bool reliableWords=words.Zip(words.Skip(1),(a,b)=>a.Time<=b.Time).All(ok=>ok)&&wordStamps.Cast<Match>().All(stamp=>int.TryParse(stamp.Groups[2].Value,out int second)&&second<60);
            foreach(Match match in stamps) {
                if(!int.TryParse(match.Groups[1].Value,out int min)||!int.TryParse(match.Groups[2].Value,out int sec)||sec>=60)continue;
                string fraction=match.Groups[3].Value;
                double decimals=fraction.Length==0?0:int.Parse(fraction,CultureInfo.InvariantCulture)/Math.Pow(10,fraction.Length);
                double lineTime=min*60.0+sec+decimals;firstTime??=lineTime;timed.Add(new(lineTime,content,words.Count==0||!reliableWords?null:words.Select(w=>w with{Time=w.Time+lineTime-firstTime.Value}).ToArray()));
            }
        }
        if(timed.Count==0)return new(plain,false,0,source,offsetMatch.Success ? offsetMatch.Groups[1].Value : null);
        var grouped=timed.OrderBy(l=>l.Time).GroupBy(l=>l.Time).Select(g=>{int offsetIndex=0;var merged=new List<LyricWord>();foreach(var row in g){if(row.Words!=null)merged.AddRange(row.Words.Select(w=>w with{Start=w.Start+offsetIndex}));offsetIndex+=row.Text.Length+1;}return new LyricLine(g.Key,string.Join("\n",g.Select(l=>l.Text)),merged.Count==0?null:merged);}).ToList();
        return new(grouped,true,offset,source,offsetMatch.Success ? offsetMatch.Groups[1].Value : null);
    }
    public static Lyrics Load(string path) => Parse(new UTF8Encoding(false,true).GetString(File.ReadAllBytes(path)),path);
}
