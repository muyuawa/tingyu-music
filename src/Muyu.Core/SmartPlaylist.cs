using System.Globalization;
namespace Muyu.Core;

public enum SmartField { Format,Artist,AlbumArtist,Album,Genre,Year,SampleRate,Bits,Favorite,PlayCount,LastPlayed,Added,Duration }
public enum SmartOperator { Equals,NotEquals,Contains,Greater,Less,WithinDays,Never }
public enum SmartSort { Title,Artist,Album,Added,LastPlayed,PlayCount,SampleRate,Duration }
public sealed record SmartRule(SmartField Field,SmartOperator Operator,string Value);
public sealed record SmartPlaylist(Guid Id,string Name,long Revision,bool MatchAll,SmartRule[] Rules,SmartSort Sort=SmartSort.Album,bool Descending=false,int Limit=0)
{
    public void Validate()
    {
        if(Id==Guid.Empty||string.IsNullOrWhiteSpace(Name)||Name.Length>100||Revision<1||Rules==null||Rules.Length is <1 or >32||Limit is <0 or >100000||!Enum.IsDefined(Sort))throw new FormatException("智能歌单名称、版本、规则数或上限无效");
        foreach(var rule in Rules)
        {
            if(rule==null||!Enum.IsDefined(rule.Field)||!Enum.IsDefined(rule.Operator)||rule.Value==null||rule.Value.Length>500)throw new FormatException("智能规则字段无效");
            var allowed=rule.Field switch {
                SmartField.Format or SmartField.Artist or SmartField.AlbumArtist or SmartField.Album or SmartField.Genre=>new[]{SmartOperator.Equals,SmartOperator.NotEquals,SmartOperator.Contains},
                SmartField.Favorite=>new[]{SmartOperator.Equals,SmartOperator.NotEquals},
                SmartField.Added=>new[]{SmartOperator.WithinDays},
                SmartField.LastPlayed=>new[]{SmartOperator.WithinDays,SmartOperator.Never},
                _=>new[]{SmartOperator.Equals,SmartOperator.NotEquals,SmartOperator.Greater,SmartOperator.Less}};
            if(!allowed.Contains(rule.Operator))throw new FormatException("此字段不支持所选比较方式");
            if(rule.Operator==SmartOperator.Never)continue;
            if(rule.Field==SmartField.Favorite){if(!bool.TryParse(rule.Value,out _))throw new FormatException("收藏条件须为 true 或 false");}
            else if(rule.Field is SmartField.Year or SmartField.SampleRate or SmartField.Bits or SmartField.PlayCount or SmartField.Duration or SmartField.Added or SmartField.LastPlayed)
            {if(!double.TryParse(rule.Value,NumberStyles.Float,CultureInfo.InvariantCulture,out var n)||!double.IsFinite(n)||n<0||rule.Operator==SmartOperator.WithinDays&&(n<=0||n>36500)||rule.Field==SmartField.Year&&n>9999||rule.Field==SmartField.Bits&&n>32||rule.Field==SmartField.SampleRate&&n>768000||rule.Field==SmartField.PlayCount&&n>int.MaxValue||rule.Field==SmartField.Duration&&n>TimeSpan.MaxValue.TotalSeconds)throw new FormatException("数值条件超出有效范围；天数为 0–36500，位深为 0–32");}
            else if(string.IsNullOrWhiteSpace(rule.Value))throw new FormatException("文本条件不能为空");
        }
    }
    public IReadOnlyList<Track> Evaluate(IEnumerable<Track> tracks,IReadOnlySet<string> favorites,IReadOnlyDictionary<string,TrackUsage> usage,DateTimeOffset now)
    {
        Validate();
        bool Match(Track track,SmartRule rule)
        {
            usage.TryGetValue(track.Id,out var stats);
            if(rule.Operator==SmartOperator.Never)return stats?.Played==null;
            if(rule.Operator==SmartOperator.WithinDays){var time=rule.Field==SmartField.Added?stats?.Added:stats?.Played;return time!=null&&time<=now&&time>=now.AddDays(-double.Parse(rule.Value,CultureInfo.InvariantCulture));}
            string text=rule.Field switch {SmartField.Format=>Path.GetExtension(track.Path).TrimStart('.'),SmartField.Artist=>track.Artist,SmartField.AlbumArtist=>track.AlbumArtist,SmartField.Album=>track.Album,SmartField.Genre=>track.Genre,_=>""};
            if(rule.Field<=SmartField.Genre)return rule.Operator switch{SmartOperator.Contains=>text.Contains(rule.Value,StringComparison.OrdinalIgnoreCase),SmartOperator.Equals=>text.Equals(rule.Value,StringComparison.OrdinalIgnoreCase),_=>!text.Equals(rule.Value,StringComparison.OrdinalIgnoreCase)};
            if(rule.Field==SmartField.Favorite){bool equal=favorites.Contains(track.Id)==bool.Parse(rule.Value);return rule.Operator==SmartOperator.Equals?equal:!equal;}
            double value=rule.Field switch {SmartField.Year=>track.Metadata?.Year??0,SmartField.SampleRate=>track.SampleRate,SmartField.Bits=>track.Bits,SmartField.PlayCount=>stats?.Plays??0,SmartField.Duration=>track.Duration,_=>0};
            double target=double.Parse(rule.Value,CultureInfo.InvariantCulture);
            return rule.Operator switch{SmartOperator.Equals=>value==target,SmartOperator.NotEquals=>value!=target,SmartOperator.Greater=>value>target,SmartOperator.Less=>value<target,_=>false};
        }
        object Key(Track t)=>Sort switch{SmartSort.Artist=>t.Artist,SmartSort.Album=>$"{t.AlbumArtist}\0{t.Album}\0{t.Disc:D4}\0{t.Number:D4}",SmartSort.Added=>usage.GetValueOrDefault(t.Id)?.Added??DateTimeOffset.MinValue,SmartSort.LastPlayed=>usage.GetValueOrDefault(t.Id)?.Played??DateTimeOffset.MinValue,SmartSort.PlayCount=>usage.GetValueOrDefault(t.Id)?.Plays??0,SmartSort.SampleRate=>t.SampleRate,SmartSort.Duration=>t.Duration,_=>t.Title};
        var filtered=tracks.Where(t=>t.InLibrary&&(MatchAll?Rules.All(r=>Match(t,r)):Rules.Any(r=>Match(t,r))));
        var ordered=Descending?filtered.OrderByDescending(Key):filtered.OrderBy(Key);
        return ordered.ThenBy(t=>t.Id,StringComparer.Ordinal).Take(Limit==0?int.MaxValue:Limit).ToArray();
    }
}
public sealed record SmartPreview(Guid PlaylistId,long RuleRevision,long LibraryRevision,Track[] Tracks)
{
    public void ValidateCurrent(SmartPlaylist playlist,long libraryRevision){if(playlist.Id!=PlaylistId||playlist.Revision!=RuleRevision||libraryRevision!=LibraryRevision)throw new InvalidOperationException("规则或音乐库已变化，请重新预览");}
}
