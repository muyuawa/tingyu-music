using System.Text.Json;
using Muyu.Core;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureVolumeBackendSwitch(string output)
    {
        current=null;savedAudio=savedAudio with{Backend=AudioBackend.WasapiExclusive,Dsd=DsdMode.Pcm,GainDb=0};volumeMode="original";RefreshVolumeControls();
        bool exclusiveLocked=!VolumeSlider.IsEnabled;
        await ApplyAudio(savedAudio with{Backend=AudioBackend.WasapiShared},false);
        bool sharedEnabled=volumeMode=="session"&&VolumeSlider.IsEnabled&&store.Get("volume-mode")=="session";
        await ApplyAudio(savedAudio with{Backend=AudioBackend.WasapiExclusive},false);
        bool returnLocked=volumeMode=="original"&&!VolumeSlider.IsEnabled;
        volumeMode="software";await ApplyAudio(savedAudio with{Backend=AudioBackend.WasapiShared},false);
        bool explicitSoftwarePreserved=volumeMode=="software"&&VolumeSlider.IsEnabled;
        bool pass=exclusiveLocked&&sharedEnabled&&returnLocked&&explicitSoftwarePreserved;
        await File.WriteAllTextAsync(Path.Combine(output,"VOLUME_SWITCH_VERIFICATION.json"),JsonSerializer.Serialize(new{pass,exclusiveLocked,sharedEnabled,returnLocked,explicitSoftwarePreserved,audio_opened=false,limitation="UI backend transition tested without opening a physical audio session"},new JsonSerializerOptions{WriteIndented=true}));
        if(!pass)throw new InvalidOperationException("Volume backend transition failed");
    }
}
