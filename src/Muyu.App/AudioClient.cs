using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Muyu.Core;

namespace Muyu.App;
public sealed class AudioClient : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1);
    private NamedPipeServerStream? pipe;
    private StreamReader? reader;
    private StreamWriter? writer;
    private Process? host;
    private bool disposed;
    public AudioStatus Status { get; private set; }
    public string Error { get; private set; } = "";
    public Guid? ItemId { get; private set; }
    public bool ContinuousCue { get; private set; }
    public AudioActual? Actual {get;private set;}
    public string[] AsioDrivers {get;private set;}=[];
    public DeviceCapability? Capability{get;private set;}
    public int? ProcessId => host is { HasExited: false } ? host.Id : null;
    private async Task Connect()
    {
        if (pipe?.IsConnected == true && host?.HasExited == false) return;
        pipe?.Dispose(); host?.Dispose();
        string name = "muyu-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string path = Path.Combine(AppContext.BaseDirectory, "audio-host", "Muyu.AudioHost.exe");
        if (!File.Exists(path)) throw new FileNotFoundException("缺少独立音频进程，请使用完整的便携包", path);
        host = Process.Start(new ProcessStartInfo(path) { Arguments = name, UseShellExecute = false, CreateNoWindow = true });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(timeout.Token);
        reader = new StreamReader(pipe); writer = new StreamWriter(pipe) { AutoFlush = true };
    }
    public async Task Send(AudioCommand command)
    {
        await gate.WaitAsync();
        bool received = false;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            await Connect();
            await writer!.WriteLineAsync(JsonSerializer.Serialize(command));
            string? line = await reader!.ReadLineAsync().WaitAsync(command.Action=="open"?TimeSpan.FromMinutes(10):TimeSpan.FromSeconds(15));
            if (line == null) throw new IOException("音频进程已退出。播放已停止，请重新选择曲目");
            var reply = JsonSerializer.Deserialize<AudioReply>(line, AudioProtocol.Options)!;
            Status = reply.Status; Error = reply.Error;
            ItemId = reply.ItemId; ContinuousCue = reply.ContinuousCue;
            Actual=reply.Actual;AsioDrivers=reply.AsioDrivers??AsioDrivers;
            Capability=reply.Capability??Capability;
            received = true;
            if (reply.Failed) throw new InvalidOperationException(reply.Error);
        }
        catch (Exception ex)
        {
            if (!received) { var failed = Status; failed.State = 4; Status = failed; }
            Error = ex.Message;
            if (ex is IOException or TimeoutException or OperationCanceledException)
            {
                pipe?.Dispose();
                if (host is { HasExited: false }) host.Kill();
            }
            throw;
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { disposed = true; pipe?.Dispose(); if (host is { HasExited: false } && !host.WaitForExit(1500)) host.Kill(); host?.Dispose(); }
        finally { gate.Release(); }
    }
}
