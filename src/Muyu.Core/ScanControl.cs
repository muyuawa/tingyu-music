namespace Muyu.Core;
public sealed class ScanControl:IDisposable
{
    private readonly ManualResetEventSlim gate=new(true);
    public bool IsPaused=>!gate.IsSet;
    public void Pause()=>gate.Reset();
    public void Resume()=>gate.Set();
    public void Checkpoint(CancellationToken cancellation)=>gate.Wait(cancellation);
    public void Dispose()=>gate.Dispose();
}
