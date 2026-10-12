using System.Text.Json;
using Microsoft.UI.Xaml;
using Muyu.Core;

namespace Muyu.App;

public sealed partial class MainWindow
{
    private long sessionRevision, rememberedFrame;
    private Guid? presentedItemId;
    private bool waitingToResume;
    private DateTime lastSessionSave;
    private string? sessionWarning;
    private double DisplayPosition => waitingToResume || audio.ItemId != presentedItemId ? resumeSeconds : audio.Status.Seconds;
    private void RestoreSessionState()
    {
        var recovery = store.ReadSession(); var restored = recovery.Session; sessionWarning = recovery.Warning;
        if (restored == null) { queue = []; index = -1; return; }
        queue = restored.Items.ToList(); index = restored.CurrentIndex; nextInsertion = index + 1; sessionRevision = restored.Revision;
        rememberedFrame = restored.Position; repeat = restored.Repeat; shuffle = restored.Shuffle; waitingToResume = index >= 0;
        removeAfterPlay=restored.RemoveAfterPlay?.ToHashSet()??[];
        playedQueueIds=restored.CompletedItems?.ToHashSet()??[];
        if (restored.OriginalOrder != null) originalQueue = restored.OriginalOrder.Select(id => queue.FirstOrDefault(i => i.ItemId == id)).OfType<QueueItem>().ToList();
        RepeatIcon.Glyph = repeat == 1 ? "\uE8ED" : "\uE8EE"; RepeatButton.Opacity = repeat == 0 ? .55 : 1; ShuffleButton.Opacity = shuffle ? 1 : .55;
    }
    private void PresentRestoredSession()
    {
        if (index >= 0 && index < queue.Count)
        {
            long position = rememberedFrame;
            queue = queue.Select(i => i with { Track = knownTracks.GetValueOrDefault(i.Track.Id) ?? i.Track }).ToList();
            PresentTrack(queue[index].Track); waitingToResume = true;
            rememberedFrame = Math.Clamp(position, 0, current!.Frames); resumeSeconds = (double)rememberedFrame / current.SampleRate;
            UpdateStoredPosition(); Show(sessionWarning ?? $"已恢复队列与上次位置 {Time(resumeSeconds)}，等待播放");
        }
        else if (sessionWarning != null) Show(sessionWarning, true);
    }
    private void CaptureReliablePosition()
    {
        if (current == null || audio.ItemId != presentedItemId || audio.Status.Rate == 0 || waitingToResume) return;
        rememberedFrame = Math.Clamp(audio.Status.Position, 0, current.Frames); resumeSeconds = (double)rememberedFrame / current.SampleRate;
    }
    private void UpdateStoredPosition()
    {
        settingProgress = true; Progress.Value = Math.Clamp(resumeSeconds, 0, Progress.Maximum); settingProgress = false;
        Elapsed.Text = Time(resumeSeconds); LyricsCanvas.SetPlaying(false); UpdateLyricsTiming();
    }
    private bool SaveQueue(bool capturePosition = true)
    {
        if (capturePosition) CaptureReliablePosition();
        bool saved = false;
        try
        {
            Guid? id = index >= 0 && index < queue.Count ? queue[index].ItemId : null;
            long position = id == presentedItemId ? rememberedFrame : 0;
            var snapshot = new PlaybackSession(1, checked(sessionRevision + 1), queue.ToArray(), id, position, repeat, shuffle, originalQueue?.Select(i => i.ItemId).ToArray(),removeAfterPlay.Where(id=>queue.Any(i=>i.ItemId==id)).ToArray(),playedQueueIds.Where(id=>queue.Any(i=>i.ItemId==id)).ToArray());
            store.SaveSession(snapshot); sessionRevision = snapshot.Revision;
            saved = true;
        }
        catch (Exception ex) { Show("播放会话未保存：" + ex.Message, true); }
        lastSessionSave = DateTime.UtcNow;
        return saved;
    }
    private async Task CloseSession()
    {
        closed = true; clock.Stop(); lifetime.Cancel(); scan?.Cancel(); StopLibraryMonitoring();
        ClosePopover();glassRenderer?.Dispose();flowingBackdrop?.Dispose();volumeTimer.Stop();volumePoll.Stop();
        seekDebounce.Stop();
        CloseSystemIntegration();
        try
        {
            if (!waitingToResume && audio.Status.State == 2)
            {
                await audio.Send(new("pause"));
                int actual = queue.FindIndex(i => i.ItemId == audio.ItemId);
                if (actual >= 0) { index = actual; current = queue[actual].Track; presentedItemId = queue[actual].ItemId; }
            }
            SaveQueue();
        }
        catch { SaveQueue(); }
        finally { await audio.DisposeAsync(); }
    }
}
