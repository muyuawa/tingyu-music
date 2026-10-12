using System.Text.Json;

namespace Muyu.Core;

public sealed record SessionRecovery(PlaybackSession? Session, string? Warning);

public sealed partial class LibraryStore
{
    public SessionRecovery ReadSession()
    {
        string? warning = null;
        foreach (string key in new[] { "playback-session", "playback-session-backup" })
        {
            string? value = Get(key); if (value == null) continue;
            try { return new(PlaybackSession.Parse(value), warning); }
            catch (Exception ex) when (ex is JsonException or FormatException) { warning = "上次会话无法完整读取，已尝试恢复备用快照"; }
        }
        try
        {
            var legacy = JsonSerializer.Deserialize<QueueItem[]>(Get("queue-v2") ?? "[]") ?? [];
            int current = legacy.Length == 0 ? -1 : Math.Clamp(JsonSerializer.Deserialize<int>(Get("queue-index") ?? "0"), 0, legacy.Length - 1);
            return new(new PlaybackSession(1, 0, legacy, current < 0 ? null : legacy[current].ItemId, 0).Validate(), warning);
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return new(null, "保存的队列无效，原始记录已保留");
        }
    }
}
