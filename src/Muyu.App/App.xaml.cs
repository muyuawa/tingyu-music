using Microsoft.UI.Xaml;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Muyu.App;
public partial class App : Application
{
    private Window? window;
    private Mutex? instance;
    private readonly CancellationTokenSource activationLifetime=new();
    public App()
    {
        UnhandledException += (_, e) => WriteCrash(e.Exception, "运行时");
        try { InitializeComponent(); }
        catch (Exception ex) { ShowStartupFailure(ex, "载入应用资源"); throw; }
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        string stage = "准备数据目录";
        try
        {
#if MUYU_DIAGNOSTICS
            var commandLine=Environment.GetCommandLineArgs();int captureIndex=Array.IndexOf(commandLine,"--capture-ui");
            if(captureIndex>=0&&captureIndex+1<commandLine.Length)Environment.SetEnvironmentVariable("MUYU_DATA_DIR",Path.Combine(Path.GetFullPath(commandLine[captureIndex+1]),"session-"+Guid.NewGuid().ToString("N")));
#endif
            string? scope = await SelectDataFolder();
            if (scope == null) { Exit(); return; }
            Environment.SetEnvironmentVariable("MUYU_DATA_DIR", scope);
            stage = "连接已有窗口";
            string name = "muyu-ui-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope.ToUpperInvariant())))[..24];
            instance = new Mutex(false, @"Local\" + name, out bool primary);
            if (!primary)
            {
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(5000);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                await writer.WriteLineAsync(JsonSerializer.Serialize(Environment.GetCommandLineArgs()));
                instance.Dispose();
                Exit();
                return;
            }
            stage = "载入主窗口";
            var recoveryWindow = window;
            var main = new MainWindow();
            window = main;
            window.Closed += (_, _) => { activationLifetime.Cancel(); instance?.Dispose(); };
            stage = "显示主窗口";
            window.Activate();
            recoveryWindow?.Close();
            _ = Task.Run(async () =>
            {
                while (!activationLifetime.IsCancellationRequested)
                {
                    try
                    {
                        using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                        await pipe.WaitForConnectionAsync(activationLifetime.Token);
                        using var reader = new StreamReader(pipe);
                        string? json = await reader.ReadLineAsync(activationLifetime.Token);
                        if (json is { Length: <= 32768 } && JsonSerializer.Deserialize<string[]>(json) is { } values)
                            main.DispatcherQueue.TryEnqueue(async () => await main.ReceiveActivation(values));
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { }
                    catch (JsonException) { }
                }
            });
        }
        catch (Exception ex)
        {
            ShowStartupFailure(ex, stage);
            activationLifetime.Cancel();
            instance?.Dispose();
            Exit();
        }
    }
    private static string WriteCrash(Exception exception, string stage)
    {
        string preferred = Environment.GetEnvironmentVariable("MUYU_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuyuMusic");
        foreach (string folder in new[] { preferred, Path.Combine(Path.GetTempPath(), "MuyuMusic") })
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "crash.log");
                File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] 听屿 {typeof(App).Assembly.GetName().Version} · {stage}{Environment.NewLine}{exception}{Environment.NewLine}");
                return path;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return "错误记录无法写入";
    }
    private static void ShowStartupFailure(Exception exception, string stage)
    {
        string log = WriteCrash(exception, stage);
        // This dialog also works when the XAML resources themselves cannot load.
        MessageBox(IntPtr.Zero, $"听屿 启动失败（{stage}）。\n\n{exception.Message}\n\n错误记录：{log}", "听屿", 0x10);
    }
    [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
    private async Task<string?> SelectDataFolder()
    {
        string folder=Muyu.Core.DataPaths.Preferred(AppContext.BaseDirectory,Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),Environment.GetEnvironmentVariable("MUYU_DATA_DIR"));
        try{Muyu.Core.DataPaths.ValidateWritable(folder);return folder;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){
            var choice=new TaskCompletionSource<string?>();var warning=new Microsoft.UI.Xaml.Controls.TextBlock{Text="数据目录不可写，请选择一个可写文件夹。\n"+folder,TextWrapping=TextWrapping.Wrap};var button=new Microsoft.UI.Xaml.Controls.Button{Content="选择数据文件夹"};var panel=new Microsoft.UI.Xaml.Controls.StackPanel{Padding=new Thickness(28),Spacing=20};panel.Children.Add(warning);panel.Children.Add(button);var recovery=new Window{Title="听屿 · 数据目录",Content=panel};window=recovery;recovery.Closed+=(_,_)=>choice.TrySetResult(null);button.Click+=async(_,_)=>{try{var picker=new Windows.Storage.Pickers.FolderPicker();picker.FileTypeFilter.Add("*");WinRT.Interop.InitializeWithWindow.Initialize(picker,WinRT.Interop.WindowNative.GetWindowHandle(recovery));var selected=await picker.PickSingleFolderAsync();if(selected==null)return;Muyu.Core.DataPaths.ValidateWritable(selected.Path);choice.TrySetResult(selected.Path);}catch(Exception error){warning.Text=error.Message;}};recovery.Activate();string? result=await choice.Task;return result;
        }
    }
}
