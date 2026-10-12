using LiquidGlassWinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.UI;
using Muyu.Core;

namespace Muyu.App;

public enum GlassMaterial { Clear, Button, Navigation, Elevated }
public sealed record GlassParameters(double Refraction=1.55,double Highlight=48,double Blur=18,double Dispersion=.75,double TintOpacity=.16,double OverlayBlur=28)
{
    public void Validate()
    {
        if(!double.IsFinite(Refraction)||Refraction is <1 or >3.5||!double.IsFinite(Highlight)||Highlight is <0 or >100||!double.IsFinite(Blur)||Blur is <0 or >32||!double.IsFinite(OverlayBlur)||OverlayBlur is <0 or >40||!double.IsFinite(Dispersion)||Dispersion is <0 or >5||!double.IsFinite(TintOpacity)||TintOpacity is <0 or >.85)throw new FormatException("玻璃参数超出有效范围");
    }
}

/// <summary>Owns live compositor backdrop materials; never captures or reads back pixels.</summary>
public sealed class GlassRenderer : IDisposable
{
    private sealed class Surface
    {
        public required FrameworkElement Host;
        public required FrameworkElement PointerHost;
        public required Action<Brush> SetBackground;
        public required GlassMaterial Material;
        public required double Radius;
        public LiquidGlassBrush? Brush;
        public long LastPointerUpdate;
        public float PointerAngle=-105;
        public int PointerUpdates;
        public bool PointerActive;
        public required SizeChangedEventHandler Resize;
        public required PointerEventHandler Move, Leave;
    }
    private readonly Dictionary<FrameworkElement, Surface> surfaces = [];
    private bool dark, reduced, reduceMotion, economy, disabled;
    private Color accent=Color.FromArgb(255,36,95,203);
    public void SetAccent(Color value){if(accent==value)return;accent=value;foreach(var surface in surfaces.Values.Where(s=>s.Material==GlassMaterial.Navigation))Apply(surface);}
    public string? Diagnostic { get; private set; }
    public GlassParameters Parameters { get; set; }=new();
    public int ActiveShaderCount => surfaces.Values.Count(s => s.Brush?.IsUsingShader == true);
    public event Action<string>? Failed;

    public GlassRenderer()
    {
        try
        {
            var pins = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "glass-runtime.json")))!;
            foreach (var pin in pins)
            {
                using var input = File.OpenRead(Path.Combine(AppContext.BaseDirectory, pin.Key));
                if (!Convert.ToHexString(SHA256.HashData(input)).Equals(pin.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("玻璃渲染组件与固定运行库版本不一致：" + pin.Key);
            }
        }
        catch (Exception error) { disabled = true; Diagnostic = error.Message; }
    }

    public void Configure(bool isDark, bool reduceTransparency, bool reducedMotion, bool lowPower)
    {
        dark = isDark; reduced = reduceTransparency; reduceMotion = reducedMotion; economy = lowPower;
        foreach (var surface in surfaces.Values) Apply(surface);
    }

    public void Attach(FrameworkElement host, GlassMaterial material, double radius, Action<Brush> setBackground,FrameworkElement? pointerHost=null)
    {
        if (surfaces.TryGetValue(host, out var existing)) { Apply(existing); return; }
        Surface? surface = null;
        surface = new Surface
        {
            Host = host, PointerHost=pointerHost??host, Material = material, Radius = radius, SetBackground = setBackground,
            Resize = (_, _) => { if (surface?.Brush is { } brush) Shape(surface, brush); },
            Move = (_, args) =>
            {
                if (surface?.Brush is not { } brush || reduceMotion || Environment.TickCount64 - surface.LastPointerUpdate < 33) return;
                surface.LastPointerUpdate = Environment.TickCount64;
                var point = args.GetCurrentPoint(host).Position;
                if(point.X<0||point.Y<0||point.X>host.ActualWidth||point.Y>host.ActualHeight){ResetPointer(surface);return;}
                UpdatePointer(surface,(float)(point.X/Math.Max(1,host.ActualWidth)),(float)(point.Y/Math.Max(1,host.ActualHeight)));
            },
            Leave = (_, _) =>
            {
                if(surface!=null)ResetPointer(surface);
            }
        };
        surfaces.Add(host, surface);
        host.SizeChanged += surface.Resize;
        // Buttons/list items can handle pointer events before the background sees them.
        surface.PointerHost.AddHandler(UIElement.PointerMovedEvent,surface.Move,true);
        surface.PointerHost.AddHandler(UIElement.PointerExitedEvent,surface.Leave,true);
        Apply(surface);
    }

    public void Detach(FrameworkElement host)
    {
        if (!surfaces.Remove(host, out var surface)) return;
        host.SizeChanged -= surface.Resize;surface.PointerHost.RemoveHandler(UIElement.PointerMovedEvent,surface.Move);surface.PointerHost.RemoveHandler(UIElement.PointerExitedEvent,surface.Leave);
        surface.SetBackground(new SolidColorBrush(Fallback(surface.Material)));
        surface.Brush = null;
    }

    private double BaseRefraction(GlassMaterial material) => Parameters.Refraction+(material==GlassMaterial.Button?.25:0);
    private void UpdatePointer(Surface surface,float x,float y)
    {
        if(surface.Brush is not {} brush||reduceMotion)return;
        float dx=x-.5f,dy=y-.5f;
        if(dx*dx+dy*dy>.0025f)
        {
            float angle=90-MathF.Atan2(dy,dx)*180/MathF.PI;
            float delta=MathF.IEEERemainder(angle-surface.PointerAngle,360);
            surface.PointerAngle+=delta;
        }
        surface.PointerActive=true;surface.PointerUpdates++;
        brush.GlareFactor=Parameters.Highlight*(dark?.9:1);
        brush.GlareAngle=surface.PointerAngle;
        brush.RefFactor=BaseRefraction(surface.Material)+.16f*Math.Min(1,MathF.Sqrt(dx*dx+dy*dy)*2);
    }
    private void ResetPointer(Surface surface)
    {
        if(surface.Brush is not {} brush||reduceMotion||!surface.PointerActive)return;
        surface.PointerActive=false;surface.PointerAngle=-105;
        brush.GlareFactor=0;
        brush.GlareAngle=-105;
        brush.RefFactor=BaseRefraction(surface.Material);
    }
    internal (int Updates,float Angle,bool Shader,bool InputSeparate,double Glare,double Rim) PointerState(FrameworkElement host)
    {
        var s=surfaces[host];return(s.PointerUpdates,s.PointerAngle,s.Brush?.IsUsingShader==true,s.PointerHost!=s.Host,s.Brush?.GlareFactor??0,s.Brush?.RefFresnelFactor??0);
    }
    internal void DiagnosticPointer(FrameworkElement host,float x,float y)=>UpdatePointer(surfaces[host],x,y);
    internal void DiagnosticLeave(FrameworkElement host)=>ResetPointer(surfaces[host]);
    private double BlurFor(GlassMaterial material)=>economy?Math.Min(2,material==GlassMaterial.Elevated?Parameters.OverlayBlur:Parameters.Blur):material==GlassMaterial.Elevated?Parameters.OverlayBlur:Parameters.Blur;
    public void PreviewBlur(double controls,double overlays)
    {
        var values=Parameters with{Blur=controls,OverlayBlur=overlays};values.Validate();Parameters=values;
        foreach(var surface in surfaces.Values)if(surface.Brush is {} brush)brush.BlurAmount=BlurFor(surface.Material);
    }
    private Color Fallback(GlassMaterial material) => dark
        ? Color.FromArgb(255, material == GlassMaterial.Elevated ? (byte)37 : (byte)26, material == GlassMaterial.Elevated ? (byte)45 : (byte)32, material == GlassMaterial.Elevated ? (byte)59 : (byte)43)
        : Color.FromArgb(255, 248, 250, 254);

    private void Apply(Surface surface)
    {
        if (reduced || disabled)
        {
            surface.SetBackground(new SolidColorBrush(Fallback(surface.Material))); surface.Brush = null; return;
        }
        var brush = surface.Brush ??= new LiquidGlassBrush();
        brush.FallbackColor = Fallback(surface.Material);
        brush.RefThickness = surface.Material == GlassMaterial.Button ? 12 : surface.Material == GlassMaterial.Navigation ? 10 : surface.Material == GlassMaterial.Clear ? 16 : 24;
        brush.RefFactor = BaseRefraction(surface.Material);
        brush.RefDispersion = economy ? 0 : Parameters.Dispersion;
        brush.DispersionRange = .35;
        brush.RefFresnelRange = 24; brush.RefFresnelHardness = 10; brush.RefFresnelFactor = dark?8:5;
        // Idle geometry retains a soft rim, without a permanent white light at the top.
        brush.GlareRange = 24; brush.GlareHardness = 12; brush.GlareFactor = surface.PointerActive&&!reduceMotion?Parameters.Highlight*(dark?.9:1):0;
        brush.GlareOppositeFactor = 25; brush.GlareConvergence = 50; brush.GlareAngle = surface.PointerAngle;
        brush.BlurAmount = BlurFor(surface.Material);
        // The compiled postprocess lerps raw -> blurred by BloomAmount. Keep
        // the floating transport fully blurred so underlapping text does not
        // compete with its controls; BlurAmount still allows clear glass at 0.
        brush.BloomAmount = surface.Material == GlassMaterial.Clear ? 1 : surface.Material == GlassMaterial.Elevated ? .8 : .25;
        brush.TintR = dark ? 19 : 247; brush.TintG = dark ? 25 : 248; brush.TintB = dark ? 38 : 252;
        if(surface.Material==GlassMaterial.Navigation){var tint=AlbumColorEngine.WithBrightness(new(accent.R,accent.G,accent.B),dark?.14:.93);brush.TintR=tint.R;brush.TintG=tint.G;brush.TintB=tint.B;}
        // A lightly frosted optical body retains its depth. It samples the live page;
        // there is no separate rectangular background underneath the capsule.
        brush.TintA = surface.Material == GlassMaterial.Clear ? Parameters.TintOpacity : surface.Material == GlassMaterial.Elevated ? Math.Max(dark?.72:.78,Parameters.TintOpacity) : surface.Material == GlassMaterial.Navigation ? Math.Max(.28,Parameters.TintOpacity) : Parameters.TintOpacity;
        brush.Saturation = surface.Material == GlassMaterial.Clear ? 1 : 1.08;
        brush.Contrast = surface.Material == GlassMaterial.Clear ? 1 : 1.03; brush.Magnification = 1;
        Shape(surface, brush);
        surface.SetBackground(brush);
        surface.Host.DispatcherQueue.TryEnqueue(() =>
        {
            if (surface.Brush?.InstanceError is not { } error || disabled) return;
            Diagnostic = error; disabled = true;
            foreach (var item in surfaces.Values) Apply(item);
            Failed?.Invoke(error);
        });
    }

    private static void Shape(Surface surface, LiquidGlassBrush brush)
    {
        double half = Math.Min(surface.Host.ActualWidth, surface.Host.ActualHeight) / 2;
        brush.ShapeRadius = half > 0 ? Math.Clamp(surface.Radius / half, 0, 1) : 1;
        brush.ShapeRoundness = surface.Material is GlassMaterial.Button or GlassMaterial.Clear ? 2 : 4;
    }

    public void Dispose() { foreach (var host in surfaces.Keys.ToArray()) Detach(host); }
}
