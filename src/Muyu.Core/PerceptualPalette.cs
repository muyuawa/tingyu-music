namespace Muyu.Core;

/// <summary>Weighted OKLab clustering and a spatially independent, abstract color mesh.</summary>
internal static class PerceptualPalette
{
    private readonly record struct Lab(double L,double A,double B)
    {
        internal double Distance(Lab other)=>Math.Pow(L-other.L,2)+Math.Pow(A-other.A,2)+Math.Pow(B-other.B,2);
    }
    private static Lab Convert(RgbColor c)
    {
        double Linear(byte x){double v=x/255.0;return v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4);}
        double r=Linear(c.R),g=Linear(c.G),b=Linear(c.B);
        double l=Math.Cbrt(.4122214708*r+.5363325363*g+.0514459929*b),m=Math.Cbrt(.2119034982*r+.6806995451*g+.1073969566*b),s=Math.Cbrt(.0883024619*r+.2817188376*g+.6299787005*b);
        return new(.2104542553*l+.7936177850*m-.0040720468*s,1.9779984951*l-2.4285922050*m+.4505937099*s,.0259040371*l+.7827717662*m-.8086757660*s);
    }
    private static RgbColor Convert(Lab c)
    {
        double l=Math.Pow(c.L+.3963377774*c.A+.2158037573*c.B,3),m=Math.Pow(c.L-.1055613458*c.A-.0638541728*c.B,3),s=Math.Pow(c.L-.0894841775*c.A-1.2914855480*c.B,3);
        byte Encode(double v)=>(byte)Math.Clamp(Math.Round(255*(v<=.0031308?12.92*v:1.055*Math.Pow(v,1/2.4)-.055)),0,255);
        return new(Encode(4.0767416621*l-3.3077115913*m+.2309699292*s),Encode(-1.2684380046*l+2.6097574011*m-.3413193965*s),Encode(-.0041960863*l-.7034186147*m+1.7076147010*s));
    }
    internal static RgbColor Ambient(RgbColor color,bool dark)
    {
        var lab=Convert(color);double chroma=Math.Sqrt(lab.A*lab.A+lab.B*lab.B);
        // Tone lightness independently of chroma: adding white to RGB erased accents.
        double target=dark?.26+.10*lab.L:.88+.04*lab.L;
        double scale=chroma>0?Math.Min(dark?.09:.11,chroma*1.15)/chroma:0;
        return Convert(new Lab(target,lab.A*scale,lab.B*scale));
    }
    internal static (RgbColor Color,double Weight)[] Cluster((RgbColor Color,double Weight)[] histogram)
    {
        var points=histogram.Select(p=>(Lab:Convert(p.Color),p.Weight)).ToArray();
        var centers=new List<Lab>{points[0].Lab};
        while(centers.Count<Math.Min(8,points.Length))
        {
            // Keep small, chromatic highlights alongside the dominant area.
            var farthest=points.OrderByDescending(p=>Math.Pow(p.Weight,.28)*(1+3*Math.Sqrt(p.Lab.A*p.Lab.A+p.Lab.B*p.Lab.B))*centers.Min(c=>c.Distance(p.Lab))).First();
            if(centers.Min(c=>c.Distance(farthest.Lab))<.002)break;
            centers.Add(farthest.Lab);
        }
        var mass=new double[centers.Count];
        for(int iteration=0;iteration<8;iteration++)
        {
            Array.Clear(mass);var sum=new Lab[centers.Count];
            foreach(var point in points)
            {
                int nearest=Enumerable.Range(0,centers.Count).MinBy(i=>centers[i].Distance(point.Lab));double w=point.Weight;mass[nearest]+=w;
                var old=sum[nearest];sum[nearest]=new(old.L+point.Lab.L*w,old.A+point.Lab.A*w,old.B+point.Lab.B*w);
            }
            for(int i=0;i<centers.Count;i++)if(mass[i]>0)centers[i]=new(sum[i].L/mass[i],sum[i].A/mass[i],sum[i].B/mass[i]);
        }
        var clustered=centers.Select((c,i)=>(Color:Convert(c),Weight:mass[i])).Where(p=>p.Weight>0).OrderByDescending(p=>p.Weight).ThenBy(p=>p.Color.ToString()).ToArray();
        // Reserve chromatic roles independently of the neutral tonal clusters. A tiny
        // cyan eye or purple detail must not compete with four shades of gray.
        var chromatic=histogram.Where(p=>{var c=Convert(p.Color);return c.A*c.A+c.B*c.B>=.045*.045;})
            .GroupBy(p=>{var c=Convert(p.Color);return (int)Math.Floor((Math.Atan2(c.B,c.A)+Math.PI)*12/(2*Math.PI));})
            .Select(group=>
            {
                double weight=group.Sum(p=>p.Weight),l=0,a=0,b=0;
                foreach(var p in group){var c=Convert(p.Color);l+=c.L*p.Weight;a+=c.A*p.Weight;b+=c.B*p.Weight;}
                return(Color:Convert(new Lab(l/weight,a/weight,b/weight)),Weight:weight);
            }).Where(p=>p.Weight>=histogram.Sum(h=>h.Weight)*.001).ToArray();
        var selected=new List<(RgbColor Color,double Weight)>{clustered[0]};
        var accents=new List<Lab>();
        foreach(int role in Enumerable.Range(0,2))
        {
            var candidates=chromatic.Where(p=>
            {
                var c=Convert(p.Color);return accents.All(a=>Math.Abs(Math.IEEERemainder(Math.Atan2(c.B,c.A)-Math.Atan2(a.B,a.A),2*Math.PI))>Math.PI/4);
            }).ToArray();
            if(candidates.Length==0)break;
            var pick=candidates.OrderByDescending(p=>
            {
                var c=Convert(p.Color);double chroma=Math.Sqrt(c.A*c.A+c.B*c.B);
                double separation=accents.Count==0?1:accents.Min(a=>Math.Abs(Math.IEEERemainder(Math.Atan2(c.B,c.A)-Math.Atan2(a.B,a.A),2*Math.PI)))/Math.PI;
                return Math.Pow(p.Weight,.2)*chroma*chroma*(.3+separation);
            }).ThenBy(p=>p.Color.ToString()).First();
            accents.Add(Convert(pick.Color));
            if(selected.All(p=>Convert(p.Color).Distance(Convert(pick.Color))>.0004))selected.Add(pick);
        }
        while(selected.Count<Math.Min(5,clustered.Length))
        {
            var remaining=clustered.Where(p=>selected.All(s=>Convert(s.Color).Distance(Convert(p.Color))>.0004)).ToArray();
            if(remaining.Length==0)break;
            var pick=remaining.OrderByDescending(p=>Math.Pow(p.Weight,.3)*selected.Min(s=>Convert(s.Color).Distance(Convert(p.Color)))).ThenBy(p=>p.Color.ToString()).First();selected.Add(pick);
        }
        return selected.ToArray();
    }
    internal static (RgbColor Color,double Weight)[] ForTheme((RgbColor Color,double Weight)[] palette,bool dark)
    {
        // Soft preferences retain the other tones instead of throwing pixels away.
        double Score((RgbColor Color,double Weight) p)
        {
            double lightness=Convert(p.Color).L,preference=dark?1-lightness:lightness;
            return Math.Pow(p.Weight,.18)*(.6+.8*preference*preference)*(1+1.8*p.Color.Saturation);
        }
        return palette.OrderByDescending(Score).ThenBy(p=>p.Color.ToString()).Select(p=>(p.Color,Weight:Score(p))).ToArray();
    }
    internal static byte[] AbstractTexture((RgbColor Color,double Weight)[] palette)
    {
        // Positions never depend on album pixels: rearranging the cover cannot reproduce its shapes.
        (double X,double Y)[] anchors=[(.16,.24),(.82,.22),(.5,.78),(.13,.83),(.88,.79)];
        var colors=palette.Select(p=>Convert(p.Color)).ToArray();var result=new byte[32*32*4];
        for(int y=0;y<32;y++)for(int x=0;x<32;x++)
        {
            double l=0,a=0,b=0,total=0;
            for(int i=0;i<colors.Length;i++)
            {
                double dx=x/31.0-anchors[i].X,dy=y/31.0-anchors[i].Y;
                // Localized smooth lobes prevent every pixel becoming the same average.
                double w=palette[i].Weight*(.002+Math.Exp(-(dx*dx+dy*dy)/.055));total+=w;
                l+=colors[i].L*w;a+=colors[i].A*w;b+=colors[i].B*w;
            }
            var c=Convert(new Lab(l/total,a/total,b/total));int offset=(y*32+x)*4;
            result[offset]=c.R;result[offset+1]=c.G;result[offset+2]=c.B;result[offset+3]=255;
        }
        return result;
    }
}
