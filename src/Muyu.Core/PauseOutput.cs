namespace Muyu.Core;

public static class PauseOutput
{
    // Capture the driver's final clock before destroying an exclusive output.
    public static async Task<bool> Execute(AudioBackend backend, Func<string,Task<bool>> command, Action capturePosition, Action released)
    {
        if(!await command("pause"))return false;
        capturePosition();
        if(backend==AudioBackend.WasapiExclusive)
        {
            if(!await command("stop"))return false;
            released();
        }
        return true;
    }
}
