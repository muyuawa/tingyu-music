using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using Muyu.Core;

namespace Muyu.App;

/// <summary>Album-colored ambient mesh, animated entirely by the compositor.</summary>
public sealed class FlowingBackdrop : IDisposable
{
    private readonly FrameworkElement host;
    private readonly Compositor compositor;
    private readonly LayerVisual scene;
    private readonly CompositionEffectFactory blurFactory;
    private readonly CompositionEffectBrush blurBrush;
    private readonly SpriteVisual background,hero;
    private readonly CompositionColorBrush canvasBrush;
    private readonly CompositionColorGradientStop heroTop;
    private readonly ArtworkBackdrop artwork;
    private AlbumScene colorScene=AlbumColorEngine.Scene(AlbumColorEngine.Fallback,false,false);
    private readonly SpriteVisual[] fields=new SpriteVisual[5];
    private readonly CompositionColorGradientStop[][] stops=new CompositionColorGradientStop[5][];
    private readonly AnimationController?[] controllers=new AnimationController?[5];
    private readonly Vector2[] anchors=[new(.08f,.15f),new(.92f,.2f),new(.15f,.92f),new(.88f,.88f),new(.5f,.5f)];
    
    private bool dark,animate,visible,foreground=true,reducedMotion;
    public bool IsAnimating=>animate&&visible&&foreground;
    public double BlurAmount { get; private set; }
    public FlowingBackdrop(FrameworkElement element)
    {
        host=element;compositor=ElementCompositionPreview.GetElementVisual(host).Compositor;
        scene=compositor.CreateLayerVisual();scene.Clip=compositor.CreateInsetClip();background=compositor.CreateSpriteVisual();scene.Children.InsertAtBottom(background);canvasBrush=compositor.CreateColorBrush();background.Brush=canvasBrush;
        hero=compositor.CreateSpriteVisual();var heroBrush=compositor.CreateLinearGradientBrush();heroBrush.StartPoint=new(0,0);heroBrush.EndPoint=new(0,1);heroTop=compositor.CreateColorGradientStop(0,default);heroBrush.ColorStops.Add(heroTop);heroBrush.ColorStops.Add(compositor.CreateColorGradientStop(1,Color.FromArgb(0,0,0,0)));hero.Brush=heroBrush;scene.Children.InsertAtTop(hero);
        using var blur=new GaussianBlurEffect{Name="BackgroundBlur",Source=new CompositionEffectSourceParameter("Backdrop"),BlurAmount=0,BorderMode=EffectBorderMode.Hard,Optimization=EffectOptimization.Balanced};
        blurFactory=compositor.CreateEffectFactory(blur,["BackgroundBlur.BlurAmount"]);blurBrush=blurFactory.CreateBrush();
        for(int i=0;i<fields.Length;i++)
        {
            var brush=compositor.CreateRadialGradientBrush();brush.EllipseCenter=new(.5f,.5f);brush.EllipseRadius=new(.5f,.5f);
            stops[i]=[compositor.CreateColorGradientStop(0,default),compositor.CreateColorGradientStop(.35f,default),compositor.CreateColorGradientStop(1,default)];
            foreach(var stop in stops[i])brush.ColorStops.Add(stop);
            var field=compositor.CreateSpriteVisual();field.Brush=brush;fields[i]=field;scene.Children.InsertAtTop(field);
            var motion=compositor.CreateVector3KeyFrameAnimation();motion.Duration=TimeSpan.FromSeconds(30+i*3.75);motion.IterationBehavior=AnimationIterationBehavior.Forever;
            var easing=compositor.CreateCubicBezierEasingFunction(new(.42f,0),new(.58f,1));
            motion.InsertKeyFrame(0,new Vector3(0,0,0));motion.InsertKeyFrame(.33f,new Vector3(.17f*(i%2==0?1:-1),-.13f,0),easing);motion.InsertKeyFrame(.66f,new Vector3(-.16f,.18f*(i%2==0?1:-1),0),easing);motion.InsertKeyFrame(1,Vector3.Zero,easing);
            field.StartAnimation(nameof(Visual.RelativeOffsetAdjustment),motion);controllers[i]=field.TryGetAnimationController(nameof(Visual.RelativeOffsetAdjustment));controllers[i]?.Pause();
            motion.Dispose();easing.Dispose();
        }
        artwork=new ArtworkBackdrop(compositor,scene);
        foreach(var field in fields){scene.Children.Remove(field);scene.Children.InsertAtTop(field);}
        ElementCompositionPreview.SetElementChildVisual(host,scene);host.SizeChanged+=SizeChanged;Resize();ApplyPalette();
    }
    private void SizeChanged(object sender,SizeChangedEventArgs args)=>Resize();
    private void Resize()
    {
        var size=new Vector2((float)host.ActualWidth,(float)host.ActualHeight);scene.Size=background.Size=size;hero.Size=new(size.X,Math.Min(280,size.Y));
        artwork.Resize(size);
        for(int i=0;i<fields.Length;i++){fields[i].Size=size*new Vector2(1.12f+(i%2)*.12f,1.35f);fields[i].Offset=new(size.X*anchors[i].X-fields[i].Size.X/2,size.Y*anchors[i].Y-fields[i].Size.Y/2,0);}
    }
    private void AnimateColor(CompositionObject target,string property,Color color,bool transition)
    {
        if(transition&&visible&&!reducedMotion){using var animation=compositor.CreateColorKeyFrameAnimation();animation.Duration=TimeSpan.FromMilliseconds(800);using var ease=compositor.CreateCubicBezierEasingFunction(new(.16f,1),new(.3f,1));animation.InsertKeyFrame(1,color,ease);target.StartAnimation(property,animation);}
        else{target.StopAnimation(property);if(target is CompositionColorBrush brush)brush.Color=color;else if(target is CompositionColorGradientStop stop)stop.Color=color;}
    }
    private void ApplyPalette(bool transition=false)
    {
        Color Convert(RgbColor c,double a=1)=>Color.FromArgb((byte)Math.Clamp(Math.Round(a*255),0,255),c.R,c.G,c.B);
        AnimateColor(canvasBrush,nameof(CompositionColorBrush.Color),Convert(colorScene.Canvas),transition);
        artwork.SetArtwork(colorScene.ArtworkRgba,colorScene.Dark,transition&&!reducedMotion&&visible);
        hero.IsVisible=false;
        AnimateColor(heroTop,nameof(CompositionColorGradientStop.Color),Convert(colorScene.Hero,colorScene.HeroAlpha),transition);
        for(int i=0;i<fields.Length;i++)
        {
            fields[i].IsVisible=true;
            for(int j=0;j<3;j++)AnimateColor(stops[i][j],nameof(CompositionColorGradientStop.Color),Convert(colorScene.Fields[i],colorScene.FieldAlpha[i]*(j==0?1:j==1?.52:0)),transition);
        }
    }
    public void SetScene(AlbumScene value)
    {
        if(ReferenceEquals(colorScene.ArtworkRgba,value.ArtworkRgba)&&colorScene.Dark==value.Dark&&colorScene.Canvas==value.Canvas&&colorScene.Hero==value.Hero&&colorScene.HeroAlpha==value.HeroAlpha&&colorScene.Fields.SequenceEqual(value.Fields)&&colorScene.FieldAlpha.SequenceEqual(value.FieldAlpha))return;
        bool transition=colorScene.Dark==value.Dark&&value.CanTransition;colorScene=value;ApplyPalette(transition);
    }
    public void SetBlur(double amount)
    {
        if(!double.IsFinite(amount))return;
        BlurAmount=Math.Clamp(amount,0,80);
        // Smooth the small abstract palette texture before it is stretched to
        // the window; blurring broad full-size gradients alone is barely visible.
        artwork.SetBlur(BlurAmount);
        blurBrush.Properties.InsertScalar("BackgroundBlur.BlurAmount",(float)BlurAmount);
        scene.Effect=BlurAmount>0?blurBrush:null;
    }
    public void Configure(bool isDark,bool enabled,bool reducedMotion,bool lowPower,double blur=0)
    {
        this.reducedMotion=reducedMotion;
        dark=isDark;
        SetBlur(blur);
        visible=enabled;host.Visibility=enabled?Visibility.Visible:Visibility.Collapsed;animate=!reducedMotion&&!lowPower;UpdateAnimation();
    }
    public void SetForeground(bool value){if(foreground==value)return;foreground=value;UpdateAnimation();}
    private void UpdateAnimation(){artwork.SetAnimating(IsAnimating);foreach(var controller in controllers)if(IsAnimating)controller?.Resume();else controller?.Pause();}
    public void Dispose()
    {
        host.SizeChanged-=SizeChanged;foreach(var controller in controllers)controller?.Dispose();foreach(var field in fields)field.StopAnimation(nameof(Visual.RelativeOffsetAdjustment));
        ElementCompositionPreview.SetElementChildVisual(host,null);artwork.Dispose();foreach(var field in fields)field.Dispose();hero.Dispose();canvasBrush.Dispose();background.Dispose();scene.Dispose();blurBrush.Dispose();blurFactory.Dispose();
    }
}
