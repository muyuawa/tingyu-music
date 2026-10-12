namespace Muyu.App;
public sealed partial class MainWindow
{
    public async Task ReceiveActivation(string[] args,bool initial=false)
    {
        try{if(closed)return;for(int i=0;Root.XamlRoot==null&&i<100;i++)await Task.Delay(50);if(Root.XamlRoot==null)return;
            if(args.Contains("--background")){SetTray(true);AppWindow.Hide();return;}
            if(!initial){AppWindow.Show();Activate();}
            int command=Array.IndexOf(args,"--import");if(command>=0&&command+1<args.Length){string path=Path.GetFullPath(args[command+1]);if(new[]{".m3u",".m3u8",".muyu-playlist"}.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase))await ImportPlaylistPath(path);else await Import([path]);}
        }catch(Exception ex){Show("未能打开所选文件："+ex.Message,true);}
    }
}
