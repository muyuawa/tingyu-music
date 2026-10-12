using Muyu.Core;
using System.Text.Json;

public static class IntegratedRegressionTests
{
    public static void Run(string root,Action<bool,string> check)
    {
        bool Reject(Action action){try{action();return false;}catch(FormatException){return true;}}
        var bindings=Shortcuts.Defaults;Shortcuts.Validate(bindings);
        check(bindings.Length==10,"Default shortcuts cover every action");
        check(Reject(()=>Shortcuts.Validate(bindings.Select(b=>b.Action==PlayerAction.Stop?b with{Key=32,Control=false}:b).ToArray())),"Duplicate shortcuts rejected before saving");
        check(Reject(()=>Shortcuts.Validate(bindings.Select(b=>b.Action==PlayerAction.Stop?b with{Key=115,Control=false,Alt=true}:b).ToArray())),"System close shortcut cannot be rebound");
        check(Reject(()=>new AudioSettings(Backend:AudioBackend.Asio,FollowSystemDefault:true).Validate()),"ASIO cannot silently follow another default device");
        string folder=Path.Combine(root,"paths-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string local=Path.Combine(folder,"local");
        check(DataPaths.Preferred(folder,local)==Path.Combine(local,"MuyuMusic"),"Installed data is separate from program files");
        File.WriteAllText(Path.Combine(folder,"portable.marker"),"");
        check(DataPaths.Preferred(folder,local)==Path.Combine(folder,"data"),"Portable marker selects adjacent data");
        check(DataPaths.Preferred(folder,local,Path.Combine(folder,"override"))==Path.Combine(folder,"override"),"Explicit data directory overrides portable marker");
        DataPaths.ValidateWritable(local);check(!Directory.EnumerateFiles(local).Any(),"Writable data probe leaves no temporary file");
        var reader=new LyricsExtension(1,"test.srt","SRT",[".srt"],@"^\d+\r?\n(?<time>\d{2}:\d{2}:\d{2}[,.]\d{3}) --> [^\r\n]+\r?\n(?<text>[\s\S]*?)(?=\r?\n\r?\n|\z)");
        string srt=Path.Combine(folder,"lyrics.srt");File.WriteAllText(srt,"1\n00:00:01,250 --> 00:00:02,000\nHello\n世界\n\n2\n00:00:03,500 --> 00:00:04,000\nNext");
        var parsed=reader.Parse(File.ReadAllText(srt),srt);
        check(parsed.Lines.Count==2&&parsed.Lines[0].Time==1.25&&parsed.Lines[0].Text=="Hello\n世界","Declarative SRT reader preserves timing and bilingual text");
        check(LyricsSources.FromFile(srt,extensions:[reader]).Parser.Contains(reader.Revision),"Lyrics extension revision participates in binding identity");
        check(reader.Revision!=(reader with{Pattern=reader.Pattern+" "}).Revision,"Changed parser rules invalidate previous binding identity");
        check(Reject(()=>(reader with{Pattern="(?<text>.*)"}).Validate()),"Lyrics extension requires explicit named timing group");
        check(Reject(()=>LyricsExtension.ValidateSet([reader,reader with{Id="test.second"} ])),"Overlapping enabled lyrics readers rejected");
        var repeated=LrcParser.Parse("[00:01.00][00:05.00]<00:01.00>One<00:01.50>Two<00:02.00>");
        check(repeated.Lines[1].Words![1].Time==5.5,"Repeated Enhanced LRC lines shift their word timestamps");
        const string reversed="[00:01.00]<00:02.00>One<00:01.00>Two";
        check(LrcParser.Parse(reversed).Lines[0].Words==null&&LrcParser.Warnings(reversed).Count>0,"Unreliable word times retain line text with explicit warning");
        string wav=Path.Combine(root,"reference-16.wav");var i=NativeAudio.Probe(wav);
        var integer=new Track(Track.Identity(wav),wav,"Integer","Artist","Album",i.SampleRate,i.Bits,i.Channels,i.Frames,Encoding:i.Encoding);
        using(var source=new NativeSource(new([new(Guid.NewGuid(),integer)]),new(TargetBits:24))){var actual=new byte[32];source.Read(actual,4,true);var original=new byte[16];NativeAudio.muyu_read_pcm(wav,0,4,original,16);var expected=Enumerable.Range(0,8).SelectMany(n=>new byte[]{0,0,original[n*2],original[n*2+1]}).ToArray();check(actual.SequenceEqual(expected),"Pure precision expansion adds no dither or sample changes");}
        string floatPath=Path.Combine(root,"extended.float.wav");var f=NativeAudio.Probe(floatPath);
        check(f.Encoding==AudioEncoding.FloatPcm&&f.Frames==i.Frames,"Float WAV is identified separately from integer PCM");
        var floating=integer with{Id=Track.Identity(floatPath),Path=floatPath,Bits=f.Bits,Encoding=f.Encoding};
        using(var source=new NativeSource(new([new(Guid.NewGuid(),floating)]),new(),new Dictionary<string,SourceFingerprint>{[floating.Id]=LocalFileIdentity.Capture(floatPath)})){check(source.Format.Encoding==AudioEncoding.FloatPcm&&source.Read(new byte[8],1,true)==1,"Verified floating source retains conversion identity through native ABI");}
        var store=new LibraryStore(Path.Combine(folder,"library"));store.AddBatch([floating]);
        check(store.ReadAll()[0].Encoding==AudioEncoding.FloatPcm,"Audio encoding survives SQLite roundtrip");
        var settings=new AudioSettings(Device:"test");var auth=new AudioAuthorization(Guid.NewGuid(),"test","caps",floating.SampleRate,floating.Bits,floating.Channels,settings.StrategyHash,DateTimeOffset.Now,AudioAuthorization.Kind(floating));
        check(auth.Matches(settings,floating,"caps")&&!auth.Matches(settings,floating with{Encoding=AudioEncoding.IntegerPcm},"caps"),"Float conversion consent cannot cover integer source identity");
        string externalPath=Path.Combine(folder,"external.wav");File.Copy(wav,externalPath);var fp=LocalFileIdentity.Capture(externalPath);var external=integer with{Id=Track.SourceIdentity(fp,0,-1),Path=externalPath,InLibrary=false};
        store.RememberSources([external],new Dictionary<string,SourceFingerprint>{[externalPath]=fp});store.ValidateSource(external);
        check(store.ReadAll().Count==1,"Queue-only source evidence does not add music to library");
        using(var changed=new FileStream(externalPath,FileMode.Open,FileAccess.Write)){changed.Position=44;changed.WriteByte(123);}
        bool blocked=false;try{store.ValidateSource(external);}catch(IOException){blocked=true;}
        check(blocked,"Changed queue-only source is rejected before playback");
        long revision=store.LibraryRevision;store.Set("favorites",JsonSerializer.Serialize(new[]{floating.Id}));
        check(store.LibraryRevision>revision,"Favorites invalidate frozen smart-playlist previews");
        var scanned=LibraryScanner.Scan([wav],CancellationToken.None);
        check(scanned.Fingerprints![Path.GetFullPath(wav)]==LocalFileIdentity.Capture(wav),"Scanner returns evidence paired with probed source");
        string exchange=Path.Combine(folder,"atomic.muyu-playlist");File.WriteAllText(exchange,"old");PlaylistExchange.Save(exchange,"new",[integer,integer]);
        check(PlaylistExchange.Import(exchange,NativeAudio.Probe).Tracks.Count==2&&!Directory.EnumerateFiles(folder,".muyu-export-*").Any(),"Atomic exchange export replaces complete document and preserves duplicates");
        string mp3=Path.Combine(folder,"rg.mp3");File.Copy(Path.Combine(root,"extended.mp3"),mp3);
        using(var file=TagLib.File.Create(mp3)){file.Tag.ReplayGainTrackGain=-7.25;file.Tag.ReplayGainTrackPeak=.875;file.Save();}
        var metadata=MetadataReader.Read(mp3).Details;
        check(metadata?.TrackGainDb==-7.25&&metadata.TrackPeak==.875,"ReplayGain is read from ID3 as well as Vorbis tags");
    }
}
