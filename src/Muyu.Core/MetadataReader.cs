using System.Buffers.Binary;
using System.Text;

namespace Muyu.Core;

public sealed record TrackMetadata(string[] Artists,string[] AlbumArtists,string[] Genres,uint Year,uint Disc,uint DiscCount,uint Number,uint TrackCount,double? TrackGainDb=null,double? AlbumGainDb=null,double? TrackPeak=null,double? AlbumPeak=null)
{
    public bool Equals(TrackMetadata? other)=>other!=null&&(Artists??[]).SequenceEqual(other.Artists??[])&&(AlbumArtists??[]).SequenceEqual(other.AlbumArtists??[])&&(Genres??[]).SequenceEqual(other.Genres??[])&&Year==other.Year&&Disc==other.Disc&&DiscCount==other.DiscCount&&Number==other.Number&&TrackCount==other.TrackCount&&TrackGainDb==other.TrackGainDb&&AlbumGainDb==other.AlbumGainDb&&TrackPeak==other.TrackPeak&&AlbumPeak==other.AlbumPeak;
    public override int GetHashCode(){var hash=new HashCode();foreach(var value in (Artists??[]).Concat(AlbumArtists??[]).Concat(Genres??[]))hash.Add(value);hash.Add(Year);hash.Add(Disc);hash.Add(Number);return hash.ToHashCode();}
}
public sealed record Metadata(string Title,string Artist,string Album,string? EmbeddedLyrics,byte[]? Cover,TrackMetadata? Details=null,string? EmbeddedCue=null);
public static class MetadataReader
{
    public static Metadata Read(string path,bool includeCover=true)
    {
        try
        {
            using var file=TagLib.File.Create(path,TagLib.ReadStyle.Average);var tag=file.Tag;
            byte[]? cover=includeCover?(tag.Pictures.FirstOrDefault(p=>p.Type==TagLib.PictureType.FrontCover)?.Data.Data??tag.Pictures.FirstOrDefault()?.Data.Data):null;
            if(cover?.Length>8*1024*1024)cover=null;
            var xiph=file.GetTag(TagLib.TagTypes.Xiph) as TagLib.Ogg.XiphComment;
            string? Field(string name)=>xiph?.GetField(name).FirstOrDefault();
            double? Finite(double number)=>double.IsFinite(number)?number:null;
            var details=new TrackMetadata(tag.Performers,tag.AlbumArtists,tag.Genres,tag.Year,tag.Disc,tag.DiscCount,tag.Track,tag.TrackCount,Finite(tag.ReplayGainTrackGain),Finite(tag.ReplayGainAlbumGain),Finite(tag.ReplayGainTrackPeak),Finite(tag.ReplayGainAlbumPeak));
            return new(string.IsNullOrWhiteSpace(tag.Title)?Path.GetFileNameWithoutExtension(path):tag.Title,
                tag.Performers.Length==0?"未知艺术家":string.Join(" / ",tag.Performers),string.IsNullOrWhiteSpace(tag.Album)?"未知专辑":tag.Album,tag.Lyrics,cover,details,Field("CUESHEET"));
        }
        catch(Exception ex) when(ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException or ArgumentException)
        {return ReadFlac(path,includeCover);}
    }
    private static Metadata ReadFlac(string path,bool includeCover)
    {
        string title=System.IO.Path.GetFileNameWithoutExtension(path),artist="未知艺术家",album="未知专辑";string? lyrics=null;byte[]? cover=null;
        using var f=File.OpenRead(path); Span<byte> magic=stackalloc byte[4];if(f.Read(magic)!=4||!magic.SequenceEqual("fLaC"u8))return new(title,artist,album,null,null);
        bool last=false;
        while(!last&&f.Position+4<=f.Length) {
            f.ReadExactly(magic);last=(magic[0]&128)!=0;int type=magic[0]&127;int length=magic[1]<<16|magic[2]<<8|magic[3];
            if(f.Position+length>f.Length)throw new InvalidDataException("FLAC 元数据不完整");
            if(type!=4&&(type!=6||!includeCover)||length>16*1024*1024){f.Seek(length,SeekOrigin.Current);continue;}
            var data=new byte[length];f.ReadExactly(data);
            try {
                int offset=0;
                uint Number(bool big=false){if(offset+4>data.Length)throw new InvalidDataException();uint value=big?BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)):BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));offset+=4;return value;}
                void Skip(uint count){if(count>data.Length-offset)throw new InvalidDataException();offset+=checked((int)count);}
                if(type==4) {
                    Skip(Number());uint count=Number();if(count>100000)throw new InvalidDataException();
                    for(uint i=0;i<count;i++){uint len=Number();if(len>data.Length-offset)throw new InvalidDataException();string comment=Encoding.UTF8.GetString(data,offset,(int)len);offset+=(int)len;int equal=comment.IndexOf('=');if(equal<1)continue;string key=comment[..equal].ToUpperInvariant(),value=comment[(equal+1)..];switch(key){case "TITLE":title=value;break;case "ARTIST":artist=value;break;case "ALBUM":album=value;break;case "LYRICS":case "UNSYNCEDLYRICS":lyrics=value;break;}}
                } else if(cover==null) {Number(true);Skip(Number(true));Skip(Number(true));for(int i=0;i<4;i++)Number(true);uint size=Number(true);if(size>0&&size<=8*1024*1024&&size<=data.Length-offset)cover=data.AsSpan(offset,(int)size).ToArray();}
            } catch(InvalidDataException) { /* A broken tag must not make the audio file unplayable. */ }
        }
        return new(title,artist,album,lyrics,cover);
    }
}
