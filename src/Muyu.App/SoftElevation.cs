using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Muyu.Core;

namespace Muyu.App;

/// <summary>Rounded, dual GPU shadows. No bitmap capture or CPU blur.</summary>
public sealed class SoftElevation : IDisposable
{
    private readonly FrameworkElement host;
    private readonly ContainerVisual root;
    private readonly ShapeVisual shape;
    private readonly CompositionRoundedRectangleGeometry geometry;
    private readonly CompositionVisualSurface surface;
    private readonly CompositionSurfaceBrush mask;
    private readonly SpriteVisual[] layers;
    private readonly DropShadow[] shadows;
    private readonly float radius;
    private readonly bool outsideOnly;
    private readonly CompositionPathGeometry outline;
    private readonly CompositionGeometricClip outsideClip;
    public SoftElevation(FrameworkElement host,float radius,bool artwork)
    {
        this.host=host;this.radius=radius;outsideOnly=!artwork;
        var compositor=ElementCompositionPreview.GetElementVisual(host).Compositor;
        root=compositor.CreateContainerVisual();geometry=compositor.CreateRoundedRectangleGeometry();
        outline=compositor.CreatePathGeometry();outsideClip=compositor.CreateGeometricClip(outline);
        var fill=compositor.CreateSpriteShape(geometry);fill.FillBrush=compositor.CreateColorBrush(Color.FromArgb(255,255,255,255));
        shape=compositor.CreateShapeVisual();shape.Shapes.Add(fill);
        surface=compositor.CreateVisualSurface();surface.SourceVisual=shape;mask=compositor.CreateSurfaceBrush(surface);
        layers=new SpriteVisual[2];shadows=new DropShadow[2];
        for(int i=0;i<2;i++)
        {
            var shadow=compositor.CreateDropShadow();shadow.Mask=mask;shadow.Color=Color.FromArgb(255,13,22,38);
            shadow.BlurRadius=artwork?(i==0?28:50):(i==0?16:34);
            shadow.Offset=new(0,artwork?(i==0?8:24):(i==0?4:12),0);
            shadow.Opacity=artwork?(i==0?.10f:.22f):(i==0?.06f:.09f);
            shadows[i]=shadow;var visual=compositor.CreateSpriteVisual();visual.Shadow=shadow;layers[i]=visual;root.Children.InsertAtTop(visual);
        }
        ElementCompositionPreview.SetElementChildVisual(host,root);host.SizeChanged+=Resize;Update();
    }
    private void Resize(object sender,SizeChangedEventArgs args)=>Update();
    private void Update()
    {
        var size=new Vector2((float)host.ActualWidth,(float)host.ActualHeight);
        root.Size=shape.Size=surface.SourceSize=geometry.Size=size;
        geometry.CornerRadius=new(Math.Min(radius,size.X/2),Math.Min(radius,size.Y/2));
        if(outsideOnly&&size.X>0&&size.Y>0)
        {
            // Transparent glass must not sample its own shadow as a grey fill.
            using var outer=CanvasGeometry.CreateRectangle(CanvasDevice.GetSharedDevice(),-100,-100,size.X+200,size.Y+200);
            using var inner=CanvasGeometry.CreateRoundedRectangle(CanvasDevice.GetSharedDevice(),0,0,size.X,size.Y,radius,radius);
            using var ring=outer.CombineWith(inner,Matrix3x2.Identity,CanvasGeometryCombine.Exclude);
            outline.Path=new CompositionPath(ring);root.Clip=outsideClip;
        }
        foreach(var layer in layers)layer.Size=size;
    }
    public void SetVisible(bool visible)=>root.IsVisible=visible;
    internal bool IsVisible=>root.IsVisible;
    public void SetArtworkStyle(bool darkArtwork,bool brightArtwork,Color ambient)
    {
        if(outsideOnly)return;
        var color=darkArtwork?AlbumColorEngine.Blend(new(13,22,38),new(ambient.R,ambient.G,ambient.B),.05):new RgbColor(13,22,38);
        foreach(var shadow in shadows)shadow.Color=Color.FromArgb(255,color.R,color.G,color.B);
        shadows[0].Opacity=brightArtwork?.12f:.10f;shadows[1].Opacity=brightArtwork?.35f:darkArtwork?.22f:.28f;
    }
    public void Dispose()
    {
        host.SizeChanged-=Resize;ElementCompositionPreview.SetElementChildVisual(host,null);
        foreach(var layer in layers)layer.Dispose();foreach(var shadow in shadows)shadow.Dispose();
        mask.Dispose();surface.Dispose();shape.Dispose();geometry.Dispose();outsideClip.Dispose();outline.Dispose();root.Dispose();
    }
}
