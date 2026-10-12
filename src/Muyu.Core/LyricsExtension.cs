using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Muyu.Core;
// Declarative local text readers. No executable, network or filesystem callbacks.
public sealed record LyricsExtension(int Version,string Id,string Name,string[] Extensions,string Pattern,bool Enabled=true)
{
    public void Validate(){if(Version!=1||Id==null||!Regex.IsMatch(Id,@"\A[a-zA-Z0-9.-]{1,80}\z")||string.IsNullOrWhiteSpace(Name)||Name.Length>100||Extensions==null||Extensions.Length is <1 or >10||Extensions.Any(e=>!Regex.IsMatch(e,@"\A\.[a-z0-9]{1,16}\z")||e is ".lrc" or ".txt")||Pattern==null||Pattern.Length is <1 or >2000)throw new FormatException("歌词扩展的名称、版本、扩展名或规则无效");try{var parser=new Regex(Pattern,RegexOptions.Multiline,TimeSpan.FromMilliseconds(100));if(!new[]{"time","text"}.All(parser.GetGroupNames().Contains))throw new FormatException("规则必须包含 time 和 text 命名组");}catch(ArgumentException ex){throw new FormatException("歌词扩展规则无效",ex);}}
    [System.Text.Json.Serialization.JsonIgnore]
    public string Revision=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this with{Enabled=true}))));
    public Lyrics Parse(string text,string source){Validate();if(text.Length>4*1024*1024)throw new FormatException("扩展歌词文件超过 4 MiB");var parser=new Regex(Pattern,RegexOptions.Multiline,TimeSpan.FromMilliseconds(100));var rows=new List<LyricLine>();var timer=Stopwatch.StartNew();try{foreach(Match match in parser.Matches(text)){if(timer.ElapsedMilliseconds>1000||rows.Count>=20000)throw new FormatException("歌词扩展解析超过限制");string raw=match.Groups["time"].Value.Replace(',','.');if(!TimeSpan.TryParse(raw,CultureInfo.InvariantCulture,out var time)||time<TimeSpan.Zero||time>TimeSpan.FromDays(7))throw new FormatException("扩展歌词含无效时间");string content=match.Groups["text"].Value.Trim();if(content.Length>0)rows.Add(new(time.TotalSeconds,content));}}catch(RegexMatchTimeoutException ex){throw new FormatException("歌词扩展规则执行超时",ex);}if(rows.Count==0)throw new FormatException("歌词扩展未找到可靠内容");return new(rows.OrderBy(r=>r.Time).GroupBy(r=>r.Time).Select(g=>new LyricLine(g.Key,string.Join('\n',g.Select(r=>r.Text)))).ToArray(),true,0,source);}
    public static void ValidateSet(IReadOnlyList<LyricsExtension> readers){foreach(var reader in readers)reader.Validate();if(readers.Count>32||readers.Select(r=>r.Id).Distinct().Count()!=readers.Count||readers.Where(r=>r.Enabled).SelectMany(r=>r.Extensions).Distinct().Count()!=readers.Where(r=>r.Enabled).Sum(r=>r.Extensions.Length))throw new FormatException("歌词扩展身份或已启用扩展名存在冲突");}
}
