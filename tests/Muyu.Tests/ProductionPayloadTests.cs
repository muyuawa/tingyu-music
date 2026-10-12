using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

static class ProductionPayloadTests
{
    public static void Run(string path,Action<bool,string> check)
    {
        using var stream=File.OpenRead(path);
        using var pe=new PEReader(stream);
        var metadata=pe.GetMetadataReader();
        var methods=metadata.MethodDefinitions.Select(h=>metadata.GetString(metadata.GetMethodDefinition(h).Name)).ToArray();
        var types=metadata.TypeDefinitions.Select(h=>metadata.GetString(metadata.GetTypeDefinition(h).Name)).ToArray();
        check(!methods.Contains("CaptureIntegratedUi"),"Production application excludes integrated screenshot harness");
        check(!types.Contains("WindowCapture"),"Production application excludes desktop capture helper");
        check(!methods.Contains("PlaybackDiagnostic"),"Production application excludes detailed diagnostic export");
        check(!methods.Contains("CaptureGlassGloss")&&!methods.Contains("CaptureVolumeBackendSwitch"),"Production application excludes visual verification entry points");
        check(methods.Contains("Root_ShortcutPreview")&&methods.Contains("Root_ShortcutReleased"),"Production application retains shortcut down/up routing");
        check(!File.Exists(Path.ChangeExtension(path,".pdb")),"Production application has no debug symbol file");
    }
}
