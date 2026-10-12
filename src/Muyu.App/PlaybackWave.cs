using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;

namespace Muyu.App;
/// <summary>A decorative playing indicator, not an audio level meter.</summary>
public sealed class PlaybackWave(FrameworkElement host) : IDisposable
{
    private bool running;
    private static IEnumerable<FrameworkElement> Bars(DependencyObject node)
    {
        if(node is FrameworkElement element&&element.Name.StartsWith("WaveBar"))yield return element;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(node);i++)foreach(var child in Bars(VisualTreeHelper.GetChild(node,i)))yield return child;
    }
    public void Configure(bool playing)
    {
        bool next=playing&&host.IsLoaded&&host.Visibility==Visibility.Visible;
        if(next==running)return;running=next;int i=0;
        foreach(var bar in Bars(host))
        {
            var visual=ElementCompositionPreview.GetElementVisual(bar);visual.CenterPoint=new(0,(float)bar.Height,0);visual.StopAnimation("Scale.Y");
            if(next)
            {
                using var motion=visual.Compositor.CreateScalarKeyFrameAnimation();motion.Duration=TimeSpan.FromMilliseconds(730+i*170);motion.IterationBehavior=Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                motion.InsertKeyFrame(0,.35f+i*.1f);motion.InsertKeyFrame(.45f,1);motion.InsertKeyFrame(1,.35f+i*.1f);visual.StartAnimation("Scale.Y",motion);
            }
            else visual.Scale=Vector3.One;
            i++;
        }
    }
    public void Dispose()=>Configure(false);
}
