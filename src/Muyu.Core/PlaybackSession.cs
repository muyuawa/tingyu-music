using System.Text.Json;

namespace Muyu.Core;

public sealed record PlaybackSession(int Version, long Revision, QueueItem[] Items, Guid? CurrentItemId, long Position,
    int Repeat = 0, bool Shuffle = false, Guid[]? OriginalOrder = null,Guid[]? RemoveAfterPlay=null,Guid[]? CompletedItems=null)
{
    public int CurrentIndex => CurrentItemId == null ? -1 : Array.FindIndex(Items, i => i.ItemId == CurrentItemId);
    public PlaybackSession Validate()
    {
        if (Version != 1 || Revision < 0 || Items == null || Items.Length > 100000 || Repeat is < 0 or > 2 || Position < 0)
            throw new FormatException("播放会话格式无效");
        if (Items.Any(i => i == null || i.ItemId == Guid.Empty || i.Track == null || string.IsNullOrWhiteSpace(i.Track.Id) || string.IsNullOrWhiteSpace(i.Track.Path) ||
                i.Track.SampleRate <= 0 || !Enum.IsDefined(i.Track.Encoding) || i.Track.Bits is <1 or >32 || i.Track.Channels is < 1 or > 8 || i.Track.TotalFrames <= 0 || i.Track.EndFrame < -1 ||
                i.Track.StartFrame < 0 || CuePlaybackRun.End(i.Track) <= i.Track.StartFrame || CuePlaybackRun.End(i.Track) > i.Track.TotalFrames || i.Track.Duration > TimeSpan.MaxValue.TotalSeconds)
            || Items.Select(i => i.ItemId).Distinct().Count() != Items.Length || (CurrentItemId!=null&&CurrentIndex<0) || (Items.Length == 0 && CurrentItemId != null))
            throw new FormatException("播放会话包含无效曲目或队列身份");
        if (OriginalOrder != null && (OriginalOrder.Length > 100000 || OriginalOrder.Distinct().Count() != OriginalOrder.Length || OriginalOrder.Any(id => id == Guid.Empty)))
            throw new FormatException("播放会话的原始顺序无效");
        if(RemoveAfterPlay!=null&&(RemoveAfterPlay.Length>Items.Length||RemoveAfterPlay.Distinct().Count()!=RemoveAfterPlay.Length||RemoveAfterPlay.Any(id=>!Items.Any(i=>i.ItemId==id))))throw new FormatException("延后移除记录无效");
        if(CompletedItems!=null&&(CompletedItems.Length>Items.Length||CompletedItems.Distinct().Count()!=CompletedItems.Length||CompletedItems.Any(id=>!Items.Any(i=>i.ItemId==id))))throw new FormatException("已播放记录无效");
        long position = CurrentIndex < 0 ? 0 : Math.Min(Position, Items[CurrentIndex].Track.Frames);
        return this with { Position = position, OriginalOrder = OriginalOrder?.Distinct().ToArray() };
    }
    public static PlaybackSession Parse(string json) => (JsonSerializer.Deserialize<PlaybackSession>(json) ?? throw new FormatException("播放会话为空")).Validate();
    public PlaybackSession Remap(IReadOnlyDictionary<string, string> paths) => this with {
        Revision = checked(Revision + 1), Items = Items.Select(i => i with { Track = RemapTrack(i.Track, paths) }).ToArray()
    };
    public static Track RemapTrack(Track track, IReadOnlyDictionary<string, string> paths) => paths.TryGetValue(track.Path, out string? replacement) ? track with { Path = replacement } : track;
}
