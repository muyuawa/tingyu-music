using System.Security.Cryptography;
namespace Muyu.Core;
public sealed record TagEditDraft(string Title,string Artist,string Album,string AlbumArtist,string Genre,uint Year,uint Disc,uint Number);
public sealed record TagEditResult(string Path,string Backup,SourceFingerprint Original,SourceFingerprint Updated,Metadata Metadata);
public static class TagEditing
{
    public static Track UpdateTrack(Track track,Metadata metadata)=>track with{Title=track.CuePath==null?metadata.Title:track.Title,Artist=track.CuePath==null?metadata.Artist:track.Artist,Album=track.CuePath==null?metadata.Album:track.Album,Metadata=metadata.Details};
    private static string AudioHash(string path)
    {
        var info=NativeAudio.Probe(path);var track=new Track(Track.Identity(path),path,"","","",info.SampleRate,info.Bits,info.Channels,info.Frames);
        using var source=new NativeSource(new PlaybackRun([new(Guid.NewGuid(),track)]),new());using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var bytes=new byte[4096*source.Format.Channels*source.Format.ContainerBits/8];long remaining=source.Format.Frames;
        while(remaining>0){int count=(int)Math.Min(4096,remaining);int got=source.Read(bytes,count,true);if(got!=count)throw new IOException("音频校验提前结束");hash.AppendData(bytes,0,got*source.Format.Channels*source.Format.ContainerBits/8);remaining-=got;}return Convert.ToHexString(hash.GetHashAndReset());
    }
    public static TagEditResult Apply(Track track,TagEditDraft draft,Action<TagEditResult> commit)
    {
        if(track.CuePath!=null)throw new InvalidOperationException("CUE 虚拟曲目请编辑 CUE 文件后通过方案预览重新导入");
        if(new[]{draft.Title,draft.Artist,draft.Album,draft.AlbumArtist,draft.Genre}.Any(s=>s.Length>2000||s.Contains('\0')))throw new FormatException("标签文本无效");
        if(LibraryScope.HasLink(track.Path))throw new IOException("不直接写入链接来源");
        var original=LocalFileIdentity.Capture(track.Path);string temp=Path.Combine(Path.GetDirectoryName(track.Path)!,".muyu-tag-"+Guid.NewGuid().ToString("N")+Path.GetExtension(track.Path));string backup=track.Path+".muyu-tag-backup-"+Guid.NewGuid().ToString("N");bool replaced=false;
        try
        {
            string before=AudioHash(track.Path);File.Copy(track.Path,temp);
            using(var file=TagLib.File.Create(temp)){file.Tag.Title=draft.Title;file.Tag.Performers=Split(draft.Artist);file.Tag.Album=draft.Album;file.Tag.AlbumArtists=Split(draft.AlbumArtist);file.Tag.Genres=Split(draft.Genre);file.Tag.Year=draft.Year;file.Tag.Disc=draft.Disc;file.Tag.Track=draft.Number;file.Save();}
            if(AudioHash(temp)!=before)throw new IOException("写入标签后音频样本校验不一致，原文件未改动");
            if(LocalFileIdentity.Capture(track.Path)!=original)throw new IOException("源文件在编辑期间已变化");
            File.Replace(temp,track.Path,backup);replaced=true;
            var result=new TagEditResult(track.Path,backup,original,LocalFileIdentity.Capture(track.Path),MetadataReader.Read(track.Path));commit(result);return result;
        }
        catch{if(replaced&&File.Exists(backup))File.Replace(backup,track.Path,null);throw;}
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    private static string[] Split(string value)=>value.Split(';',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
}
