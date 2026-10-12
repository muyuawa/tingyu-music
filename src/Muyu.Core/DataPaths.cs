namespace Muyu.Core;
public static class DataPaths
{
    public static string Preferred(string applicationFolder,string localData,string? overrideFolder=null)=>Path.GetFullPath(overrideFolder??(File.Exists(Path.Combine(applicationFolder,"portable.marker"))?Path.Combine(applicationFolder,"data"):Path.Combine(localData,"MuyuMusic")));
    public static void ValidateWritable(string folder)
    {
        Directory.CreateDirectory(folder);string probe=Path.Combine(folder,".muyu-write-"+Guid.NewGuid().ToString("N"));
        using(var stream=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){stream.WriteByte(0);}
    }
}
