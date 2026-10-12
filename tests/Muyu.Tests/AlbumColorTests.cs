using Muyu.Core;
internal static class AlbumColorTests
{
    internal static void Run(Action<bool,string> check,Track track)
    {
        bool covered=true;double clearance=double.MaxValue;
        foreach(var size in new System.Numerics.Vector2[]{new(1440,940),new(1100,1100),new(884,1080),new(2560,1440),new(800,2000),new(7680,2160)})
        for(int layer=0;layer<2;layer++)for(int angle=0;angle<360;angle++)foreach(float drift in new[]{0f,.5f,1f})
        {
            var geometry=Muyu.App.BackdropLayout.Calculate(size,layer);var movement=Muyu.App.BackdropLayout.Drift(layer);
            double cx=geometry.Offset.X+geometry.Pivot.X+movement.X*size.X*drift,cy=geometry.Offset.Y+geometry.Pivot.Y+movement.Y*size.Y*drift;
            double cos=Math.Cos(angle*Math.PI/180),sin=Math.Sin(angle*Math.PI/180);
            foreach(var corner in new System.Numerics.Vector2[]{new(0,0),new(size.X,0),new(0,size.Y),size})
            {
                double x=corner.X-cx,y=corner.Y-cy;
                double margin=geometry.Side/2-Math.Max(Math.Abs(x*cos+y*sin),Math.Abs(-x*sin+y*cos));
                clearance=Math.Min(clearance,margin);covered&=margin>=80;
            }
        }
        check(covered&&clearance>=80,"Rotating backgrounds cover every viewport corner at every degree and full drift with an 80 DIP blur margin");
        byte[] Solid(RgbColor c)=>Enumerable.Range(0,1024).SelectMany(_=>new byte[]{c.R,c.G,c.B,255}).ToArray();
        var black=AlbumColorEngine.Analyze(Solid(new(0,0,0)));var white=AlbumColorEngine.Analyze(Solid(new(255,255,255)));
        check(black.IsDark&&black.IsMonochrome&&black.DominantColor==new RgbColor(0,0,0),"Black cover keeps its actual dominant color and dark archetype");
        check(white.IsBright&&white.IsMonochrome&&white.DominantColor==new RgbColor(255,255,255),"White cover keeps its actual dominant color and bright archetype");
        check(black.VibrantColor==AlbumColorEngine.Silver&&white.VibrantColor==AlbumColorEngine.Silver,"Monochrome fallback uses neutral silver");
        check(black.DeepAmbientColor.Brightness is >=.08 and <=.16&&white.LightAmbientColor.Brightness is >=.90 and <=.96,"Ambient roles normalize perceived brightness without grey rejection");
        var earth=AlbumColorEngine.Analyze(Solid(new(133,116,100)));check(earth.HasArtwork&&earth.DominantColor==new RgbColor(133,116,100),"Muted earth colors are retained");
        var pixels=Solid(new(8,8,8));for(int i=0;i<32;i++){pixels[i*4]=10;pixels[i*4+1]=220;pixels[i*4+2]=240;}
        var accents=AlbumColorEngine.Analyze(pixels);check(accents.DominantColor==new RgbColor(8,8,8)&&accents.Colors.Any(c=>c.G>180&&c.B>180),"Small vivid accents coexist with a dark dominant area");
        check(!AlbumColorEngine.Analyze([]).HasArtwork&&!AlbumColorEngine.Analyze([1,2,3]).HasArtwork&&!AlbumColorEngine.Analyze(new byte[4096]).HasArtwork,"Missing malformed and fully transparent input use baseline fallback");
        check(Math.Abs(AlbumColorEngine.Contrast(new(0,0,0),new(255,255,255))-21)<1e-9,"WCAG contrast uses linearized sRGB and black-white ratio 21");
        check(!AlbumColorEngine.Scene(black,false,true).Dark&&!AlbumColorEngine.Scene(black,false,false).Dark&&AlbumColorEngine.Scene(white,true,true).Dark,"Explicit light and dark themes remain authoritative for any cover");
        var fallback=AlbumColorEngine.Scene(AlbumColorEngine.Fallback,false,false);check(fallback.Canvas==new RgbColor(248,249,250)&&AlbumColorEngine.Scene(AlbumColorEngine.Fallback,true,true).Canvas==new RgbColor(17,21,29),"No artwork uses exact porcelain and graphite baselines");
        var rng=new Random(7919);bool roles=true,contrast=true,composites=true,lyricAlpha=true;
        for(int n=0;n<350;n++)
        {
            var colors=Enumerable.Range(0,64).SelectMany(_=>new byte[]{(byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256),255}).ToArray();var palette=AlbumColorEngine.Analyze(colors);
            roles&=palette.DeepAmbientColor.Brightness is >=.08 and <=.16&&palette.LightAmbientColor.Brightness is >=.90 and <=.96;
            foreach(bool dark in new[]{false,true})foreach(bool playing in new[]{false,true})
            {
                var scene=AlbumColorEngine.Scene(palette,dark,playing,n%2==0?new RgbColor((byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256)):null);
                contrast&=scene.TextContrast>=4.5&&AlbumColorEngine.WorstContrast(scene.SecondaryText,scene.MinimumBackground,scene.MaximumBackground)>=4.5&&AlbumColorEngine.WorstContrast(scene.Accent,scene.MinimumBackground,scene.MaximumBackground)>=4.5;
                foreach(bool linear in new[]{false,true})for(int j=0;j<8;j++)
                {
                    RgbColor Mix(RgbColor a,RgbColor b,double alpha)=>linear?AlbumColorEngine.BlendLinear(a,b,alpha):AlbumColorEngine.Blend(a,b,alpha);
                    var bg=scene.Canvas;for(int i=0;i<5;i++)bg=Mix(bg,scene.Fields[i],scene.FieldAlpha[i]*rng.NextDouble());
                    composites&=AlbumColorEngine.Contrast(scene.Text,bg)>=4.5;
                    lyricAlpha&=AlbumColorEngine.Contrast(Mix(bg,scene.Text,Math.Ceiling(scene.MinimumTextOpacity*255)/255),bg)>=4.5;
                }
            }
        }
        check(roles,"Random cover palettes maintain ambient role bounds");check(contrast,"All generated scene foreground accent and secondary colors meet 4.5 over conservative envelopes");
        check(composites,"Random animated field composites meet 4.5 in encoded and linear RGB blending");check(lyricAlpha,"Contrast constrained lyric opacity meets 4.5 over randomized field composites");
        bool transitions=true;
        foreach(bool dark in new[]{false,true})foreach(bool playing in new[]{false,true})for(int n=0;n<60;n++)
        {
            AlbumScene Make()=>AlbumColorEngine.Scene(AlbumColorEngine.Analyze(Solid(new((byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256)))),dark,playing);
            var old=Make();var next=AlbumColorEngine.GuardTransition(old,Make());if(!next.CanTransition)continue;
            foreach(bool linear in new[]{false,true})for(int t=0;t<=8;t++)
            {
                double progress=t/8.0;RgbColor Mix(RgbColor a,RgbColor b,double alpha)=>linear?AlbumColorEngine.BlendLinear(a,b,alpha):AlbumColorEngine.Blend(a,b,alpha);
                var bg=Mix(old.Canvas,next.Canvas,progress);
                for(int i=0;i<5;i++)bg=Mix(bg,Mix(old.Fields[i],next.Fields[i],progress),(old.FieldAlpha[i]*(1-progress)+next.FieldAlpha[i]*progress)*rng.NextDouble());
                transitions&=AlbumColorEngine.Contrast(next.Text,bg)>=4.5&&AlbumColorEngine.Contrast(next.SecondaryText,bg)>=4.5&&AlbumColorEngine.Contrast(Mix(bg,next.Text,next.MinimumTextOpacity),bg)>=4.5;
            }
        }
        check(transitions,"Same-theme album transition samples preserve readable text and lyric opacity");
        var playerScene=AlbumColorEngine.Scene(earth,false,true);var libraryScene=AlbumColorEngine.Scene(earth,false,false);
        check(playerScene.Canvas==libraryScene.Canvas&&playerScene.Fields.SequenceEqual(libraryScene.Fields)&&playerScene.FieldAlpha.SequenceEqual(libraryScene.FieldAlpha)&&playerScene.Dark==libraryScene.Dark,"Library and player share identical background colors and fields");
        var stage=Solid(new(130,27,25));
        for(int y=0;y<32;y++)for(int x=8;x<24;x++)
        {
            var c=x is >=14 and <=17?new RgbColor(250,225,125):new RgbColor(100,100,105);int p=(y*32+x)*4;stage[p]=c.R;stage[p+1]=c.G;stage[p+2]=c.B;
        }
        var theatre=AlbumColorEngine.Analyze(stage);
        check(theatre.Colors.Any(c=>c.Saturation<.1)&&theatre.Colors.Any(c=>c.R>200&&c.G>180)&&theatre.Colors.Any(c=>c.R>c.G*2),"Red curtains retain neutral stage and warm yellow light in the separated palette");
        check(theatre.ArtworkRgba!.SequenceEqual(stage),"Color analysis retains raw samples separately from the rendered abstract mesh");
        var rearranged=stage.Chunk(4).Reverse().SelectMany(p=>p).ToArray();var moved=AlbumColorEngine.Analyze(rearranged);
        check(theatre.BackdropRgba!.SequenceEqual(moved.BackdropRgba!)&&!theatre.BackdropRgba.SequenceEqual(stage),"Rearranging cover pixels leaves the abstract background identical: cover patterns never reach rendering");
        bool imageContrast=true,paletteDiversity=true;
        foreach(bool dark in new[]{false,true})
        {
            var scene=AlbumColorEngine.Scene(theatre,dark,true);var rendered=AlbumColorEngine.BackdropPixels(scene.ArtworkRgba!,scene.Dark);
            for(int i=0;i<rendered.Length;i+=4){var c=new RgbColor(rendered[i],rendered[i+1],rendered[i+2]);imageContrast&=AlbumColorEngine.Contrast(c,scene.Text)>=5;}
            var samples=rendered.Chunk(4).ToArray();
            paletteDiversity&=samples.Any(c=>c[0]-c[1]>10)&&samples.Any(c=>c[1]-c[2]>5);
        }
        check(imageContrast,"Abstract palette texture normalization keeps current lyrics above 5:1 across all generated pixels");
        check(paletteDiversity,"Abstract texture retains both warm and red color families without source geometry");
        check(ReferenceEquals(AlbumColorEngine.Scene(theatre,false,true).ArtworkRgba,AlbumColorEngine.Scene(theatre,false,false).ArtworkRgba),"Page navigation reuses one abstract texture without changing colors");
        check(theatre.DarkBackdropRgba!.SequenceEqual(moved.DarkBackdropRgba!)&&!theatre.LightBackdropRgba!.SequenceEqual(theatre.DarkBackdropRgba),"Both theme meshes ignore cover geometry and use different tonal priorities");
        var varied=Enumerable.Range(0,1024).SelectMany(i=>{var c=new RgbColor[]{new(12,30,80),new(245,225,100),new(220,55,150),new(30,210,215)}[i%4];return new byte[]{c.R,c.G,c.B,255};}).ToArray();
        var spectrum=AlbumColorEngine.Analyze(varied);
        check(spectrum.LightColors[0].Brightness>spectrum.DarkColors[0].Brightness,"Light theme prioritizes lighter cover tones while dark theme prioritizes darker tones");
        bool multipleHues=true,rangeSafe=true;
        foreach(bool dark in new[]{false,true})
        {
            var scene=AlbumColorEngine.Scene(spectrum,dark,true);
            var mesh=AlbumColorEngine.BackdropPixels(scene.ArtworkRgba!,dark).Chunk(4).ToArray();
            multipleHues&=mesh.Any(c=>c[0]-c[1]>12)&&mesh.Any(c=>c[2]-c[0]>12)&&scene.Fields.Distinct().Count()>=3;
            rangeSafe&=mesh.All(c=>AlbumColorEngine.Contrast(new(c[0],c[1],c[2]),scene.Text)>=5);
        }
        check(multipleHues,"Multiple separated warm cool and accent colors survive rendering in both themes");
        check(rangeSafe,"Theme-dependent multicolor meshes preserve current lyric contrast above 5:1");
        var subtle=Enumerable.Range(0,1024).SelectMany(i=>
        {
            var c=i<8?new RgbColor(30,190,210):i<60?new RgbColor(156,101,182):new RgbColor[]{new(100,89,98),new(146,133,142),new(53,37,47),new(193,185,192)}[i%4];
            return new byte[]{c.R,c.G,c.B,255};
        }).ToArray();
        var detailed=AlbumColorEngine.Analyze(subtle);
        check(detailed.Colors.Any(c=>c.G-c.R>60&&c.B-c.R>60)&&detailed.Colors.Any(c=>c.B-c.G>35&&c.R-c.G>25),"Sub-one-percent cyan and purple details survive several large neutral tonal regions");
        var detailedScene=AlbumColorEngine.Scene(detailed,false,true);
        check(detailedScene.Fields.Any(c=>c.G-c.R>35)&&detailedScene.Fields.Any(c=>c.B-c.G>25),"Light ambient tone mapping keeps visibly distinct cyan and purple chroma");
        bool textureEnvelope=true;
        for(int n=0;n<24;n++)
        {
            var mixture=Enumerable.Range(0,8).Select(_=>new RgbColor((byte)rng.Next(256),(byte)rng.Next(256),(byte)rng.Next(256))).ToArray();
            var full=AlbumColorEngine.Analyze(Enumerable.Range(0,1024).SelectMany(i=>{var c=mixture[i%mixture.Length];return new byte[]{c.R,c.G,c.B,255};}).ToArray());
            foreach(bool dark in new[]{false,true})
            {
                var scene=AlbumColorEngine.Scene(full,dark,true);
                textureEnvelope&=scene.TextContrast>=5&&AlbumColorEngine.WorstContrast(scene.SecondaryText,scene.MinimumBackground,scene.MaximumBackground)>=4.5;
                var mesh=AlbumColorEngine.BackdropPixels(scene.ArtworkRgba!,dark);
                for(int i=0;i<mesh.Length;i+=4)textureEnvelope&=AlbumColorEngine.Contrast(new(mesh[i],mesh[i+1],mesh[i+2]),scene.Text)>=5;
            }
        }
        check(textureEnvelope,"Random full abstract meshes and conservative mixed-color envelopes keep readable text after chroma-preserving tone mapping");
        var settings=new AudioSettings(Device:"test",TargetRate:44100,GainDb:-7.4);var grant=AudioPolicyGrant.Accept(settings,track,"capability");
        check(grant.Matches(settings with{GainDb=3.1,Revision=99},track with{SampleRate=96000,Bits=24},"capability"),"Policy authorization survives gain steps revisions and source sample formats");
        check(!grant.Matches(settings with{TargetRate=48000},track,"capability")&&!grant.Matches(settings with{Device="other"},track,"capability")&&!grant.Matches(settings,track,"changed"),"New output rate device or capability requires new authorization");
        check(!grant.Matches(settings,track with{Channels=6},"capability")&&!grant.Matches(settings,track with{Encoding=AudioEncoding.FloatPcm},"capability"),"New channel or decoding kind cannot borrow a PCM policy grant");
    }
}
