namespace Muyu.Core;

// One output clock across compatible physical files and CUE ranges.
public sealed class PlaybackRun
{
    public IReadOnlyList<QueueItem> Items { get; }
    public long[] Boundaries { get; }
    public bool Continuous => Items.Count > 1;
    public PlaybackRun(IEnumerable<QueueItem> entries)
    {
        var items=entries.ToArray();
        if(items.Length is <1 or >100000||items.Select(i=>i.ItemId).Distinct().Count()!=items.Length)throw new ArgumentException("连续播放序列无效");
        var first=items[0].Track;
        if(items.Any(i=>i.ItemId==Guid.Empty||i.Track.StartFrame<0||i.Track.Frames<=0||CuePlaybackRun.End(i.Track)>i.Track.TotalFrames||!Compatible(first,i.Track)))throw new ArgumentException("连续播放格式或范围不一致");
        Items=Array.AsReadOnly(items);Boundaries=new long[items.Length];long sum=0;
        for(int i=0;i<items.Length;i++)Boundaries[i]=sum=checked(sum+items[i].Track.Frames);
    }
    public static bool Compatible(Track a,Track b)=>a.SampleRate==b.SampleRate&&a.Bits==b.Bits&&a.Channels==b.Channels&&a.Encoding==b.Encoding&&a.SampleRate>0;
    public static QueueItem[] FromQueue(IReadOnlyList<QueueItem> queue,int current,bool continuous=true)
    {
        if(current<0||current>=queue.Count)return [];
        var result=new List<QueueItem>{queue[current]};
        if(continuous)for(int i=current+1;i<queue.Count&&result.Count<100000&&Compatible(result[0].Track,queue[i].Track);i++)result.Add(queue[i]);
        return result.ToArray();
    }
    public int Index(long frame){int i=Array.BinarySearch(Boundaries,Math.Max(0,frame));return Math.Min(Items.Count-1,i>=0?i+1:~i);}
    public QueueItem At(long frame)=>Items[Index(frame)];
    public long SeekFrame(Guid id,long frame){int i=Items.ToList().FindIndex(x=>x.ItemId==id);if(i<0)throw new ArgumentException("定位曲目已过期");return (i==0?0:Boundaries[i-1])+Math.Clamp(frame,0,Items[i].Track.Frames-1);}
    public AudioReply Reply(AudioStatus status,string error,bool failed=false){if(status.Rate==0)return new(status,error,failed);int i=Index(status.Position);status.Position=Math.Clamp(status.Position-(i==0?0:Boundaries[i-1]),0,Items[i].Track.Frames);status.Duration=Items[i].Track.Frames;return new(status,error,failed,Items[i].ItemId,Continuous);}
    public PlaybackRun Limit(long duration){int i=Array.IndexOf(Boundaries,duration);if(i<0)throw new InvalidOperationException("连续播放边界不一致");return new(Items.Take(i+1));}
}
