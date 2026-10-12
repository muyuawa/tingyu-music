using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Muyu.Core;
using Windows.Graphics.DirectX;

namespace Muyu.App;

/// <summary>Two abstract palette layers, uploaded once per cover/theme and moved on the GPU.</summary>
internal sealed class ArtworkBackdrop : IDisposable
{
    private readonly Compositor compositor;
    private readonly ContainerVisual root;
    private readonly CompositionGraphicsDevice graphics;
    private readonly List<Frame> retired=[];
    private Frame? frame;
    private byte[]? source;
    private bool dark,animating;
    private long transitionId;
    private Vector2 size;
    internal bool HasArtwork=>frame!=null;
    private sealed class Frame : IDisposable
    {
        internal required ContainerVisual Visual;
        internal required CompositionDrawingSurface Surface;
        internal readonly List<SpriteVisual> Sprites=[];
        internal readonly List<CompositionSurfaceBrush> Brushes=[];
        internal readonly List<AnimationController> Controllers=[];
        public void Dispose(){foreach(var sprite in Sprites){sprite.StopAnimation("RotationAngleInDegrees");sprite.StopAnimation("RelativeOffsetAdjustment");sprite.Dispose();}foreach(var controller in Controllers)controller.Dispose();foreach(var brush in Brushes)brush.Dispose();Surface.Dispose();Visual.Dispose();}
    }
    internal ArtworkBackdrop(Compositor compositor,ContainerVisual parent)
    {
        this.compositor=compositor;root=compositor.CreateContainerVisual();parent.Children.InsertAtTop(root);
        graphics=CanvasComposition.CreateCompositionGraphicsDevice(compositor,CanvasDevice.GetSharedDevice());
    }
    internal void SetArtwork(byte[]? rgba,bool isDark,bool transition)
    {
        if(ReferenceEquals(source,rgba)&&dark==isDark)return;
        source=rgba;dark=isDark;
        var previous=frame;frame=null;
        if(rgba is {Length:4096})
        {
            var surface=graphics.CreateDrawingSurface(new Windows.Foundation.Size(32,32),Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized,Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            using(var bitmap=CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(),AlbumColorEngine.BackdropPixels(rgba,dark),32,32,DirectXPixelFormat.R8G8B8A8UIntNormalized))
            using(var blur=new GaussianBlurEffect{Source=bitmap,BlurAmount=1.25f,BorderMode=EffectBorderMode.Hard})
            using(var draw=CanvasComposition.CreateDrawingSession(surface))draw.DrawImage(blur);
            frame=new Frame{Visual=compositor.CreateContainerVisual(),Surface=surface};root.Children.InsertAtTop(frame.Visual);
            for(int i=0;i<2;i++)
            {
                var brush=compositor.CreateSurfaceBrush(surface);brush.Stretch=CompositionStretch.UniformToFill;
                var sprite=compositor.CreateSpriteVisual();sprite.Brush=brush;sprite.Opacity=i==0?1:.38f;
                frame.Visual.Children.InsertAtTop(sprite);frame.Sprites.Add(sprite);frame.Brushes.Add(brush);
                using var spin=compositor.CreateScalarKeyFrameAnimation();spin.Duration=TimeSpan.FromSeconds(i==0?150:180);spin.IterationBehavior=AnimationIterationBehavior.Forever;spin.InsertKeyFrame(0,i==0?0:150);spin.InsertKeyFrame(1,i==0?360:-210);sprite.StartAnimation("RotationAngleInDegrees",spin);
                if(sprite.TryGetAnimationController("RotationAngleInDegrees") is {} rotation)frame.Controllers.Add(rotation);
                using var drift=compositor.CreateVector3KeyFrameAnimation();drift.Duration=TimeSpan.FromSeconds(36+i*9);drift.IterationBehavior=AnimationIterationBehavior.Forever;drift.InsertKeyFrame(0,Vector3.Zero);drift.InsertKeyFrame(.5f,BackdropLayout.Drift(i));drift.InsertKeyFrame(1,Vector3.Zero);sprite.StartAnimation("RelativeOffsetAdjustment",drift);
                if(sprite.TryGetAnimationController("RelativeOffsetAdjustment") is {} movement)frame.Controllers.Add(movement);
            }
            Resize(size);SetAnimating(animating);
        }
        if(previous!=null)
        {
            long generation=++transitionId;
            if(transition&&frame!=null)
            {
                retired.Add(previous);frame.Visual.Opacity=0;
                var batch=compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                using var fade=compositor.CreateScalarKeyFrameAnimation();fade.Duration=TimeSpan.FromMilliseconds(800);fade.InsertKeyFrame(1,1);frame.Visual.StartAnimation("Opacity",fade);
                // Keep the old frame opaque underneath until the new one covers it completely.
                // Fading both frames exposes the canvas by 25% halfway through a crossfade.
                batch.Completed+=(_,_)=>{if(generation==transitionId)ClearRetired();batch.Dispose();};batch.End();
            }
            else{ClearRetired();root.Children.Remove(previous.Visual);previous.Dispose();}
        }
    }
    internal void Resize(Vector2 value)
    {
        size=value;root.Size=value;
        foreach(var current in retired)ResizeFrame(current,value);
        if(frame!=null)ResizeFrame(frame,value);
    }
    private static void ResizeFrame(Frame value,Vector2 viewport)
    {
        value.Visual.Size=viewport;
        for(int i=0;i<value.Sprites.Count;i++)
        {
            var geometry=BackdropLayout.Calculate(viewport,i);var sprite=value.Sprites[i];
            // AnchorPoint stays zero: CenterPoint is relative to that anchor, not the visual's top-left.
            sprite.Size=new(geometry.Side,geometry.Side);sprite.CenterPoint=geometry.Pivot;sprite.Offset=geometry.Offset;
        }
    }
    internal void SetDiagnosticProgress(float rotation,float drift)
    {
        if(frame==null)return;
        foreach(var sprite in frame.Sprites)
        {
            if(sprite.TryGetAnimationController("RotationAngleInDegrees") is {} spin)spin.Progress=rotation;
            if(sprite.TryGetAnimationController("RelativeOffsetAdjustment") is {} movement)movement.Progress=drift;
        }
    }
    internal void SetAnimating(bool value){animating=value;if(frame!=null)foreach(var controller in frame.Controllers)if(value)controller.Resume();else controller.Pause();}
    private void ClearRetired(){foreach(var old in retired){root.Children.Remove(old.Visual);old.Dispose();}retired.Clear();}
    public void Dispose(){frame?.Dispose();foreach(var old in retired)old.Dispose();retired.Clear();graphics.Dispose();root.Dispose();}
}
