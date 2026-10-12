namespace Muyu.Core;

// A run is a snapshot of adjacent queue entries in one physical source.
// The native stream spans the run; track identity follows the output clock.
public sealed class CuePlaybackRun
{
    public IReadOnlyList<QueueItem> Items { get; }
    public long StartFrame => Items[0].Track.StartFrame;
    public long EndFrame => End(Items[^1].Track);
    public bool Continuous => Items.Count > 1;
    public Track StreamTrack => Items[0].Track with { EndFrame = EndFrame };

    public CuePlaybackRun(IEnumerable<QueueItem> entries)
    {
        var items = entries.ToArray();
        if (items.Length == 0 || items.Length > 100000 || items.Select(e => e.ItemId).Distinct().Count() != items.Length)
            throw new ArgumentException("连续播放序列无效");
        foreach (var item in items)
        {
            var track = item.Track;
            if (track.SampleRate <= 0 || track.StartFrame < 0 || End(track) <= track.StartFrame || End(track) > track.TotalFrames)
                throw new ArgumentException("连续播放的样本范围无效");
        }
        for (int i = 1; i < items.Length; i++)
            if (!Adjacent(items[i - 1].Track, items[i].Track)) throw new ArgumentException("连续播放只能包含同一音源的相邻 CUE 分轨");
        Items = Array.AsReadOnly(items);
    }
    public static QueueItem[] FromQueue(IReadOnlyList<QueueItem> queue, int current, bool allowContinuous = true)
    {
        if (current < 0 || current >= queue.Count) return [];
        var result = new List<QueueItem> { queue[current] };
        if (allowContinuous)
            for (int i = current + 1; i < queue.Count && result.Count < 100000 && Adjacent(result[^1].Track, queue[i].Track); i++) result.Add(queue[i]);
        return result.ToArray();
    }
    public static bool Adjacent(Track left, Track right) =>
        left.CuePath != null && right.CuePath != null &&
        string.Equals(left.CuePath, right.CuePath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase) &&
        left.SampleRate == right.SampleRate && left.Bits == right.Bits && left.Channels == right.Channels && left.TotalFrames == right.TotalFrames &&
        End(left) == right.StartFrame;

    public QueueItem At(long streamPosition)
    {
        long absolute = StartFrame + Math.Clamp(streamPosition, 0, EndFrame - StartFrame);
        int lo = 0, hi = Items.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (Items[mid].Track.StartFrame <= absolute) lo = mid; else hi = mid - 1; }
        return Items[lo];
    }
    public AudioReply Reply(AudioStatus status, string error, bool failed = false)
    {
        if (status.Rate == 0) return new(status, error, failed);
        var active = At(status.Position);
        status.Position = Math.Clamp(StartFrame + status.Position - active.Track.StartFrame, 0, active.Track.Frames);
        status.Duration = active.Track.Frames;
        return new(status, error, failed, active.ItemId, Continuous);
    }
    public long SeekFrame(Guid itemId, long relativeFrame)
    {
        var item = Items.FirstOrDefault(i => i.ItemId == itemId) ?? throw new ArgumentException("歌词或进度定位来自过期曲目");
        return item.Track.StartFrame - StartFrame + Math.Clamp(relativeFrame, 0, item.Track.Frames - 1);
    }
    public CuePlaybackRun Limit(long nativeDuration)
    {
        long end = StartFrame + nativeDuration;
        var kept = Items.TakeWhile(i => End(i.Track) <= end).ToArray();
        if (kept.Length == 0 || End(kept[^1].Track) != end) throw new InvalidOperationException("音频返回了不匹配的分轨边界");
        return new(kept);
    }
    public static long End(Track track) => track.EndFrame < 0 ? track.TotalFrames : track.EndFrame;
}
