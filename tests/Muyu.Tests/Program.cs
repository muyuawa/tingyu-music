using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO.Pipes;
using Muyu.Core;

int passed = 0;
void Check(bool condition, string description) { if (!condition) throw new Exception(description); passed++; Console.WriteLine("PASS " + description); }
void Reject(Action action, string description) { bool threw = false; try { action(); } catch (FormatException) { threw = true; } Check(threw, description); }
string root = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/test-fixtures");
Directory.CreateDirectory(root);
var lyrics = LrcParser.Parse("[ar:Artist]\n[offset:250]\n[00:01.2]A\n[00:02.03][00:03.004]B\n[00:02.03]Translation");
Check(lyrics.Lines.Count == 3, "LRC repeated timestamps grouped");
Check(lyrics.Lines[1].Text == "B\nTranslation", "LRC bilingual paragraph");
Check(Math.Abs(lyrics.Lines[0].Time - 1.2) < .000001 && Math.Abs(lyrics.Lines[2].Time - 3.004) < .000001, "LRC fraction precision");
Check(lyrics.ActiveIndex(.94) == -1 && lyrics.ActiveIndex(.95) == 0, "LRC positive raw offset advances presentation");
Check(lyrics.ActiveIndex(.95, .25) == -1 && lyrics.ActiveIndex(1.2, .25) == 0, "User delay offset is independent");
var plain = LrcParser.Parse("one\ntwo");
Check(!plain.Timed && plain.Lines.Count == 2 && plain.ActiveIndex(90) == -1, "Plain lyrics do not invent play focus");
Check(LrcParser.Parse("[00:99]invalid").Lines.Count == 0, "Invalid LRC seconds rejected");
string wav = Path.Combine(root, "reference-16.wav");
var info = NativeAudio.Probe(wav);
Check(info is { SampleRate: 44100, Bits: 16, Channels: 2, Frames: 44100 }, "Native probe ABI");
var pcm = new byte[16];
Check(NativeAudio.muyu_read_pcm(wav, 4096, 4, pcm, pcm.Length) == 4, "Managed native sample read ABI");
Check(NativeAudio.muyu_read_pcm(wav, 0, int.MaxValue, pcm, pcm.Length) < 0, "Oversized PCM request rejected without overflow");
string cuePath = Path.Combine(root, "album.cue");
File.WriteAllText(cuePath, "PERFORMER \"Artist\"\nTITLE \"Album\"\nFILE \"reference-16.wav\" WAVE\n TRACK 01 AUDIO\n TITLE \"First\"\n INDEX 01 00:00:00\n TRACK 02 AUDIO\n TITLE \"Second\"\n INDEX 00 00:00:35\n INDEX 01 00:00:37\n", new UTF8Encoding(false));
var cue = CueParser.Load(cuePath, NativeAudio.Probe);
Check(cue.Count == 2 && cue[0].EndFrame == cue[1].StartFrame && cue[1].EndFrame == info.Frames, "CUE contiguous sample boundaries");
Check(cue[0].Title == "First" && cue[1].Album == "Album" && cue[1].Artist == "Artist", "CUE metadata inheritance");
Check(cue[1].StartFrame == 21756, "CUE 75-frame timestamp conversion");
string invalid = Path.Combine(root, "invalid.cue");
File.WriteAllText(invalid, "FILE \"reference-16.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:75");
Reject(() => CueParser.Load(invalid, NativeAudio.Probe), "Invalid CUE frame index rejected");
File.WriteAllText(invalid, "FILE \"reference-16.wav\" WAVE\nTRACK 01 AUDIO\nINDEX 01 00:00:30\nTRACK 02 AUDIO\nINDEX 01 00:00:20");
Reject(() => CueParser.Load(invalid, NativeAudio.Probe), "Reverse CUE segments rejected");
var physical = new Track(Track.Identity(wav), wav, "Whole", "Artist", "Album", info.SampleRate, info.Bits, info.Channels, info.Frames);
string database = Path.Combine(root, "database-" + Guid.NewGuid().ToString("N"));
var store = new LibraryStore(database);
store.AddBatch([physical]); store.AddBatch(cue);
Check(store.ReadAll().Count == 2 && store.ReadAll().All(t => t.CuePath != null), "CUE import replaces physical duplicate");
store.AddBatch([physical]); store.AddBatch(cue);
Check(store.ReadAll().Count == 2, "Rescan does not duplicate physical or CUE tracks");
store.Set("test", "中文 value"); Check(new LibraryStore(database).Get("test") == "中文 value", "SQLite persistence");
var queue = new PlaybackQueue(); queue.Replace([cue[0]]); queue.AddNext(cue[1]); queue.AddNext(physical);
Check(queue.Items[1].Track == cue[1] && queue.Items[2].Track == physical, "Play-next insertion retains click order");
queue.AddNext(cue[1]); Check(queue.Items.Select(i => i.ItemId).Distinct().Count() == 4, "Repeated tracks retain separate queue identities");
Check(!queue.Remove(queue.Items[0].ItemId), "Removing current queue entry requires explicit playback decision");
var packet = new AudioReply(new AudioStatus { State = 2, Rate = 96000, Position = 96001, Shared = 0 }, "");
var roundtrip = JsonSerializer.Deserialize<AudioReply>(JsonSerializer.Serialize(packet, AudioProtocol.Options), AudioProtocol.Options)!;
Check(roundtrip.Status.Position == 96001 && roundtrip.Status.Rate == 96000, "Audio process protocol preserves sample clock");
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); bool stopped = false; try { LibraryScanner.Scan([root], cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
    Check(stopped, "Scanner cancellation");
}
var scanned = LibraryScanner.Scan([wav, cuePath], CancellationToken.None);
Check(scanned.Tracks.Count == 2 && scanned.Errors.Count == 0, "Scanner CUE deduplication");
var committed = new List<Track>(); LibraryScanner.Scan([wav, cuePath], CancellationToken.None, commit: batch => committed.AddRange(batch));
Check(committed.Count == 2 && committed.All(t => t.CuePath != null), "Scanner commits complete CUE batches");

var first = new QueueItem(Guid.NewGuid(), cue[0]);
var second = new QueueItem(Guid.NewGuid(), cue[1]);
var plan = new CuePlaybackRun([first, second]);
Check(plan.Continuous && plan.StreamTrack.StartFrame == 0 && plan.StreamTrack.EndFrame == info.Frames, "Continuous CUE uses one physical stream range");
Check(plan.At(cue[1].StartFrame - 1).ItemId == first.ItemId && plan.At(cue[1].StartFrame).ItemId == second.ItemId, "CUE identity follows exact output boundary");
Check(plan.Reply(new AudioStatus { State = 2, Rate = info.SampleRate, Position = cue[1].StartFrame + 7 }, "").Status.Position == 7, "CUE progress becomes relative to active track");
Check(plan.Reply(new AudioStatus { State = 3, Rate = info.SampleRate, Position = info.Frames }, "").Status.Position == cue[1].Frames, "CUE end reports last track duration");
Check(plan.SeekFrame(second.ItemId, 123) == cue[1].StartFrame + 123, "CUE seek maps relative track frame to stream");
Check(plan.SeekFrame(second.ItemId, long.MaxValue) == info.Frames - 1, "CUE seek clamps to selected track");
var secondOnly = new CuePlaybackRun([second]);
Check(secondOnly.At(0).ItemId == second.ItemId && secondOnly.SeekFrame(second.ItemId, 123) == 123, "Mid-album run retains relative origin");
Check(CuePlaybackRun.FromQueue([first, second], 0, false).Length == 1, "Single-track repeat disables continuous run");
Check(CuePlaybackRun.FromQueue([first, second, new(Guid.NewGuid(), cue[0])], 0).Length == 2, "Repeated earlier track starts a new stream");
Check(!CuePlaybackRun.Adjacent(cue[0], cue[1] with { Path = "different.wav" }), "Different source cannot join CUE stream");
Check(!CuePlaybackRun.Adjacent(cue[0], cue[1] with { SampleRate = 48000 }), "Different sample rate cannot join CUE stream");
Check(!CuePlaybackRun.Adjacent(cue[0], cue[1] with { Bits = 24 }), "Different valid depth cannot join CUE stream");
Check(!CuePlaybackRun.Adjacent(cue[0], cue[1] with { StartFrame = cue[1].StartFrame + 1 }), "Gap in CUE ranges is not hidden");
Check(!CuePlaybackRun.Adjacent(cue[0], cue[1] with { StartFrame = cue[1].StartFrame - 1 }), "Overlapping CUE ranges are not joined");
var limited = plan.Limit(cue[0].Frames);
Check(!limited.Continuous && limited.Items.Count == 1 && limited.EndFrame == cue[0].EndFrame, "Queue edit discards future CUE identities");
bool stale = false; try { limited.SeekFrame(second.ItemId, 0); } catch (ArgumentException) { stale = true; }
Check(stale, "Removed CUE entry cannot issue a stale seek");
bool duplicate = false; try { new CuePlaybackRun([first, first]); } catch (ArgumentException) { duplicate = true; }
Check(duplicate, "Duplicate queue identity is rejected");
store.AddBatch([cue[0] with { Title = "Zebra" }, cue[1] with { Title = "Apple" }]);
Check(store.ReadAll()[0].StartFrame == 0, "CUE library order follows source samples, not titles");
var diagnostic = new AudioReply(new AudioStatus { BufferedFrames = 22050, BufferCapacity = 88200, Underruns = 3 }, "", ItemId: second.ItemId, ContinuousCue: true);
var diagnosticCopy = JsonSerializer.Deserialize<AudioReply>(JsonSerializer.Serialize(diagnostic, AudioProtocol.Options), AudioProtocol.Options)!;
Check(diagnosticCopy.Status.BufferedFrames == 22050 && diagnosticCopy.Status.Underruns == 3 && diagnosticCopy.ItemId == second.ItemId && diagnosticCopy.ContinuousCue, "Read-ahead diagnostics and CUE identity survive IPC");

WorkflowTests.Run(root, Check);
TrackTimeSortTests.Run(root, Check);
ScanReuseTests.Run(root, Check);
LibraryRecoveryTests.Run(root, Check);

if (args.Length > 1)
{
    string pipeName = "muyu-" + Guid.NewGuid().ToString("N");
    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    using var host = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[1])) { Arguments = pipeName, UseShellExecute = false, CreateNoWindow = true })!;
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        async Task<AudioReply> Send(AudioCommand command)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(command));
            return JsonSerializer.Deserialize<AudioReply>((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!, AudioProtocol.Options)!;
        }
        Check((await Send(new("status"))).Status.State == 0, "Independent audio host starts idle");
        Check((await Send(new("mute",Frame:1))).Failed && (await Send(new("status"))).Status.State == 0, "Mute without an active output cannot start audio or change clock");
        Check((await Send(new("invalid-command"))).Failed && (await Send(new("status"))).Status.State == 0, "Unknown host command is rejected without changing playback");
        Check((await Send(new("open", cue[0], "not-a-device", Segments: [first, first]))).Failed, "Host rejects duplicate run before opening output");
        Check((await Send(new("status"))).Status.BufferCapacity == 0, "Idle host diagnostic ABI");
        var missing = physical with { Path = Path.Combine(root, "does-not-exist.wav") };
        Check((await Send(new("open", missing, "not-a-device"))).Failed, "Host reports missing source without opening output");
        Check((await Send(new("status"))).Status.State == 4, "Host retains error state");
        Check((await Send(new("stop"))).Status.State == 0, "Host stops and resets state");
        await writer.WriteLineAsync(JsonSerializer.Serialize(new AudioCommand("quit")));
        await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.ExitCode == 0, "Independent audio host shuts down cleanly");
    }
    finally { if (!host.HasExited) host.Kill(); }
}

// Silent media is suitable for a later, explicitly initiated interactive review.
string preview = Path.Combine(root, "silent-preview"); Directory.CreateDirectory(preview);
using (var output = new BinaryWriter(File.Create(Path.Combine(preview, "暮色之间.wav"))))
{
    const int dataSize = 44100 * 4 * 90;
    output.Write("RIFF"u8); output.Write(36 + dataSize); output.Write("WAVEfmt "u8); output.Write(16); output.Write((short)1); output.Write((short)2); output.Write(44100); output.Write(176400); output.Write((short)4); output.Write((short)16); output.Write("data"u8); output.Write(dataSize);
    var silence = new byte[176400]; for (int i = 0; i < 90; i++) output.Write(silence);
}
File.WriteAllText(Path.Combine(preview, "暮色之间.lrc"), "[00:00.00]把喧嚣留在身后\n[00:06.00]让晚风慢慢经过\n[00:12.00]沿着海岸走向夜色\n[00:18.00]每个音符都有归处\n[00:24.00]海面倒映着星光\n[00:30.00]此刻只听见远方\n[00:36.00]让思绪随风飘荡\n[00:42.00]再缓缓回到身旁\n[00:48.00]把喧嚣留在身后\n[01:00.00]沿着海岸走向夜色\n[01:12.00]每个音符都有归处", new UTF8Encoding(false));
await CompletionTests.Run(root,Check);
TagEditReferenceTests.Run(root,Check);
IntegratedRegressionTests.Run(root,Check);
var pauseEvents=new List<string>();
async Task<bool> PauseCase(AudioBackend backend,string? failure=null){pauseEvents.Clear();return await PauseOutput.Execute(backend,action=>{pauseEvents.Add(action);return Task.FromResult(action!=failure);},()=>pauseEvents.Add("capture"),()=>pauseEvents.Add("released"));}
Check(await PauseCase(AudioBackend.WasapiExclusive)&&pauseEvents.SequenceEqual(new[]{"pause","capture","stop","released"}),"Exclusive pause captures final clock before releasing output");
Check(await PauseCase(AudioBackend.WasapiShared)&&pauseEvents.SequenceEqual(new[]{"pause","capture"}),"Shared pause retains its output");
Check(await PauseCase(AudioBackend.Asio)&&pauseEvents.SequenceEqual(new[]{"pause","capture"}),"ASIO pause retains driver behavior");
Check(!await PauseCase(AudioBackend.WasapiExclusive,"pause")&&pauseEvents.SequenceEqual(new[]{"pause"}),"Failed pause never clears the saved position or releases output");
Check(!await PauseCase(AudioBackend.WasapiExclusive,"stop")&&pauseEvents.SequenceEqual(new[]{"pause","capture","stop"}),"Failed release retains captured clock without claiming success");
AlbumColorTests.Run(Check,physical);
ShortcutRoutingTests.Run(Check);
if(args.Length>2)ProductionPayloadTests.Run(args[2],Check);
Console.WriteLine($"ALL {passed} CHECKS PASSED. No audio device was opened.");
