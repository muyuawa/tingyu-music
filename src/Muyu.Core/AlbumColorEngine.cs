namespace Muyu.Core;

public readonly record struct RgbColor(byte R,byte G,byte B)
{
    public double Brightness=>(.2126*R+.7152*G+.0722*B)/255;
    public double Saturation {get{double max=Math.Max(R,Math.Max(G,B)),min=Math.Min(R,Math.Min(G,B));return (max-min)/(max+1e-5);}}
    public double Luminance=>.2126*Linear(R)+.7152*Linear(G)+.0722*Linear(B);
    private static double Linear(byte channel){double v=channel/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
    public override string ToString()=>$"#{R:X2}{G:X2}{B:X2}";
}
public sealed record AlbumPalette(bool HasArtwork,double AverageBrightness,double MeanSaturation,RgbColor DominantColor,RgbColor VibrantColor,RgbColor DeepAmbientColor,RgbColor LightAmbientColor,RgbColor[] Colors)
{
    // Raw samples are retained for analysis only; rendering uses the abstract palette texture.
    public byte[]? ArtworkRgba { get; init; }
    public byte[]? BackdropRgba { get; init; }
    public byte[]? LightBackdropRgba { get; init; }
    public byte[]? DarkBackdropRgba { get; init; }
    public RgbColor[] LightColors { get; init; }=[];
    public RgbColor[] DarkColors { get; init; }=[];
    public bool IsDark=>HasArtwork&&AverageBrightness<.35;
    public bool IsBright=>HasArtwork&&AverageBrightness>.75;
    public bool IsMonochrome=>MeanSaturation<.15;
    public RgbColor ContrastTextColor=>IsDark?AlbumColorEngine.LightText:AlbumColorEngine.DarkText;
}
public sealed record AlbumScene(bool Dark,bool Playing,RgbColor Canvas,RgbColor[] Fields,double[] FieldAlpha,RgbColor Hero,double HeroAlpha,RgbColor MinimumBackground,RgbColor MaximumBackground,RgbColor Text,RgbColor SecondaryText,RgbColor Accent,double MinimumTextOpacity,bool CanTransition=true)
{
    public byte[]? ArtworkRgba { get; init; }
    public double TextContrast=>AlbumColorEngine.WorstContrast(Text,MinimumBackground,MaximumBackground);
}

/// <summary>Pure color analysis. No track identifiers, UI state, file I/O or per-frame work.</summary>
public static class AlbumColorEngine
{
    public static readonly RgbColor LightText=new(244,247,251),DarkText=new(23,32,45),Silver=new(192,200,214),Blue=new(36,95,203);
    public static readonly AlbumPalette Fallback=new(false,.5,0,new(128,135,147),Blue,new(17,21,29),new(248,249,250),[new(128,135,147),Blue,new(192,200,214)]);
    public static RgbColor Blend(RgbColor a,RgbColor b,double amount)
    {
        amount=Math.Clamp(amount,0,1);byte Mix(byte x,byte y)=>(byte)Math.Clamp(Math.Round(x*(1-amount)+y*amount),0,255);
        return new(Mix(a.R,b.R),Mix(a.G,b.G),Mix(a.B,b.B));
    }
    public static RgbColor WithBrightness(RgbColor color,double target)
    {
        target=Math.Clamp(target,0,1);double y=color.Brightness;
        return target>=y?Blend(color,new(255,255,255),(target-y)/Math.Max(1e-9,1-y)):Blend(color,new(0,0,0),1-target/Math.Max(1e-9,y));
    }
    public static RgbColor BlendLinear(RgbColor a,RgbColor b,double amount)
    {
        double Decode(byte c){double v=c/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
        byte Mix(byte x,byte y){double v=Decode(x)*(1-amount)+Decode(y)*amount;return (byte)Math.Clamp(Math.Round(255*(v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055)),0,255);}
        return new(Mix(a.R,b.R),Mix(a.G,b.G),Mix(a.B,b.B));
    }
    public static double Contrast(RgbColor a,RgbColor b)=>(Math.Max(a.Luminance,b.Luminance)+.05)/(Math.Min(a.Luminance,b.Luminance)+.05);
    public static double WorstContrast(RgbColor text,RgbColor minimum,RgbColor maximum)=>Math.Min(Contrast(text,minimum),Contrast(text,maximum));
    private static double MinimumOpacity(RgbColor ink,RgbColor minimum,RgbColor maximum,double target)
    {
        double low=0,high=1;
        for(int i=0;i<20;i++)
        {
            double middle=(low+high)/2;double contrast=new[]{Contrast(Blend(minimum,ink,middle),minimum),Contrast(Blend(maximum,ink,middle),maximum),Contrast(BlendLinear(minimum,ink,middle),minimum),Contrast(BlendLinear(maximum,ink,middle),maximum)}.Min();
            if(contrast>=target)high=middle;else low=middle;
        }
        return Math.Min(1,high+.01);
    }
    public static AlbumScene GuardTransition(AlbumScene? previous,AlbumScene next)
    {
        if(previous==null||previous.Dark!=next.Dark)return next with{CanTransition=false};
        RgbColor Min(RgbColor a,RgbColor b)=>new(Math.Min(a.R,b.R),Math.Min(a.G,b.G),Math.Min(a.B,b.B));
        RgbColor Max(RgbColor a,RgbColor b)=>new(Math.Max(a.R,b.R),Math.Max(a.G,b.G),Math.Max(a.B,b.B));
        var lo=Min(previous.MinimumBackground,next.MinimumBackground);var hi=Max(previous.MaximumBackground,next.MaximumBackground);
        for(int i=0;i<next.Fields.Length;i++)
        {
            double alpha=Math.Ceiling(Math.Max(previous.FieldAlpha[i],next.FieldAlpha[i])*255)/255;
            var low=Min(previous.Fields[i],next.Fields[i]);var high=Max(previous.Fields[i],next.Fields[i]);
            lo=Min(lo,Blend(lo,low,alpha));hi=Max(hi,BlendLinear(hi,high,alpha));
        }
        // A rare extreme color change switches immediately rather than flashing unreadable text.
        if(WorstContrast(next.Text,lo,hi)<5)return next with{CanTransition=false};
        RgbColor Readable(RgbColor ink){for(int i=0;WorstContrast(ink,lo,hi)<4.65&&i<40;i++)ink=Blend(ink,next.Text,.12);return ink;}
        return next with{MinimumBackground=lo,MaximumBackground=hi,SecondaryText=Readable(next.SecondaryText),Accent=Readable(next.Accent),MinimumTextOpacity=MinimumOpacity(next.Text,lo,hi,4.65)};
    }
    public static AlbumPalette Analyze(ReadOnlySpan<byte> rgba)
    {
        if(rgba.Length==0||rgba.Length%4!=0)return Fallback;
        var bins=new Dictionary<int,(double R,double G,double B,double Weight)>();double weight=0,y=0,s=0;
        for(int i=0;i<rgba.Length;i+=4)
        {
            double a=rgba[i+3]/255.0;if(a==0)continue;
            var color=new RgbColor(rgba[i],rgba[i+1],rgba[i+2]);weight+=a;y+=color.Brightness*a;s+=color.Saturation*a;
            int key=(color.R/16<<8)|(color.G/16<<4)|color.B/16;var old=bins.GetValueOrDefault(key);
            bins[key]=(old.R+color.R*a,old.G+color.G*a,old.B+color.B*a,old.Weight+a);
        }
        if(weight==0)return Fallback;
        var ranked=bins.OrderByDescending(p=>p.Value.Weight).ThenBy(p=>p.Key).Select(p=>(Color:new RgbColor((byte)Math.Round(p.Value.R/p.Value.Weight),(byte)Math.Round(p.Value.G/p.Value.Weight),(byte)Math.Round(p.Value.B/p.Value.Weight)),Weight:p.Value.Weight)).ToArray();
        var dominant=ranked[0].Color;
        var accents=ranked.OrderByDescending(p=>Math.Sqrt(p.Weight)*p.Color.Saturation*p.Color.Saturation*(.3+.7*Math.Sin(Math.PI*p.Color.Brightness))).Select(p=>p.Color).ToArray();
        var vibrant=s/weight<.15?Silver:accents[0];
        var clusters=PerceptualPalette.Cluster(ranked);
        var colors=clusters.Select(p=>p.Color).ToArray();
        var average=new RgbColor((byte)Math.Round(bins.Values.Sum(p=>p.R)/weight),(byte)Math.Round(bins.Values.Sum(p=>p.G)/weight),(byte)Math.Round(bins.Values.Sum(p=>p.B)/weight));
        var light=PerceptualPalette.ForTheme(clusters,false);var dark=PerceptualPalette.ForTheme(clusters,true);
        var lightMesh=rgba.Length==32*32*4?PerceptualPalette.AbstractTexture(light):null;
        var darkMesh=rgba.Length==32*32*4?PerceptualPalette.AbstractTexture(dark):null;
        return new(true,y/weight,s/weight,dominant,vibrant,WithBrightness(Blend(average,dark[0].Color,.45),.12),WithBrightness(Blend(average,light[0].Color,.45),.93),colors)
        {ArtworkRgba=rgba.Length==32*32*4?rgba.ToArray():null,BackdropRgba=lightMesh,LightBackdropRgba=lightMesh,DarkBackdropRgba=darkMesh,LightColors=light.Select(p=>p.Color).ToArray(),DarkColors=dark.Select(p=>p.Color).ToArray()};
    }
    public static AlbumScene Scene(AlbumPalette palette,bool globalDark,bool playing,RgbColor? customAccent=null)
    {
        bool dark=globalDark;
        var baseline=dark?new RgbColor(17,21,29):new RgbColor(248,249,250);
        // Latest user direction: both pages share the exact same full-window background.
        var canvas=palette.HasArtwork?(dark?palette.DeepAmbientColor:palette.LightAmbientColor):baseline;
        var accent=customAccent??palette.VibrantColor;
        var roles=dark?palette.DarkColors:palette.LightColors;if(roles.Length==0)roles=palette.Colors;
        var sources=Enumerable.Range(0,5).Select(i=>roles[i%roles.Length]).ToArray();
        // Preserve channel differences in a contrast-safe range, without a gray wash.
        RgbColor Field(RgbColor c)=>PerceptualPalette.Ambient(c,dark);
        var fields=sources.Select(Field).ToArray();
        double[] alphas=[.72,.65,.58,.50,.40];
        double heroAlpha=0;
        (RgbColor Min,RgbColor Max) Envelope()
        {
            var lo=canvas;var hi=canvas;
            void Layer(RgbColor c,double a){a=Math.Ceiling(a*255)/255;var l=Blend(lo,c,a);var h=BlendLinear(hi,c,a);lo=new(Math.Min(lo.R,l.R),Math.Min(lo.G,l.G),Math.Min(lo.B,l.B));hi=new(Math.Max(hi.R,h.R),Math.Max(hi.G,h.G),Math.Max(hi.B,h.B));}
            for(int i=0;i<fields.Length;i++)Layer(fields[i],alphas[i]);
            return(lo,hi);
        }
        var text=dark?LightText:DarkText;var bounds=Envelope();
        // Only the generated palette mesh reaches the renderer. Original cover geometry is discarded.
        var artwork=dark?palette.DarkBackdropRgba:palette.LightBackdropRgba;
        if(artwork!=null)
        {
            var rendered=BackdropPixels(artwork,dark);var lo=canvas;var hi=canvas;
            void Include(RgbColor c){lo=new(Math.Min(lo.R,c.R),Math.Min(lo.G,c.G),Math.Min(lo.B,c.B));hi=new(Math.Max(hi.R,c.R),Math.Max(hi.G,c.G),Math.Max(hi.B,c.B));}
            for(int i=0;i<rendered.Length;i+=4)Include(new(rendered[i],rendered[i+1],rendered[i+2]));
            foreach(var field in fields)Include(field);
            bounds=(lo,hi);
        }
        for(int attempt=0;WorstContrast(text,bounds.Min,bounds.Max)<5&&attempt<40;attempt++){for(int i=0;i<alphas.Length;i++)alphas[i]*=.9;heroAlpha*=.9;bounds=Envelope();}
        var secondary=Blend(canvas,text,Math.Max(.72,MinimumOpacity(text,bounds.Min,bounds.Max,4.65)));
        for(int i=0;WorstContrast(secondary,bounds.Min,bounds.Max)<4.65&&i<20;i++)secondary=Blend(secondary,text,.15);
        for(int i=0;WorstContrast(accent,bounds.Min,bounds.Max)<4.65&&i<40;i++)accent=Blend(accent,text,.12);
        return new(dark,playing,canvas,fields,alphas,palette.DominantColor,heroAlpha,bounds.Min,bounds.Max,text,secondary,accent,MinimumOpacity(text,bounds.Min,bounds.Max,4.65)){ArtworkRgba=artwork};
    }
    public static byte[] BackdropPixels(ReadOnlySpan<byte> rgba,bool dark)
    {
        var result=new byte[rgba.Length];
        for(int i=0;i<rgba.Length;i+=4)
        {
            double alpha=rgba[i+3]/255.0;
            var mapped=PerceptualPalette.Ambient(Blend(new(128,128,128),new(rgba[i],rgba[i+1],rgba[i+2]),alpha),dark);
            result[i]=mapped.R;result[i+1]=mapped.G;result[i+2]=mapped.B;
            result[i+3]=255;
        }
        return result;
    }
}
