using System.Security.Cryptography;
using System.Text;
namespace Muyu.Core;
public static class CoverCache
{
    public static string Key(string path){var file=new FileInfo(path);string directory=Path.GetDirectoryName(path)!;string sidecars=string.Join('|',new[]{"cover.jpg","folder.jpg","cover.png","folder.png"}.Select(name=>new FileInfo(Path.Combine(directory,name))).Where(f=>f.Exists).Select(f=>$"{f.Name}/{f.Length}/{f.LastWriteTimeUtc.Ticks}"));return Track.Identity(path)+"-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.Length}/{file.LastWriteTimeUtc.Ticks}/{sidecars}")))[..16];}
    public static void Trim(string directory,long maximum=256*1024*1024){var files=new DirectoryInfo(directory).GetFiles("*.img").OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();long total=0;foreach(var file in files){total+=file.Length;if(total>maximum)try{file.Delete();}catch(IOException){}catch(UnauthorizedAccessException){}}}
}
