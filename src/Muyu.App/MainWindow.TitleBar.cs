using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private RectInt32[] captionRegions=[];
    private RectInt32[] passthroughRegions=[];
    private void UpdateTitleBarRegions()
    {
        if(Root.XamlRoot==null||Root.ActualWidth<=0)return;
        double scale=Root.XamlRoot.RasterizationScale;
        int right=Math.Max(0,(int)(Root.ActualWidth*scale)-AppWindow.TitleBar.RightInset),height=(int)Math.Ceiling(48*scale);
        var interactive=new List<RectInt32>();
        foreach(var element in new FrameworkElement[]{SearchBox,HeaderActions,ExitPlayerButton})
        {
            if(element.Visibility!=Visibility.Visible||element.ActualWidth<=0)continue;
            var point=element.TransformToVisual(Root).TransformPoint(new(0,0));
            interactive.Add(new((int)Math.Floor(point.X*scale),(int)Math.Floor(point.Y*scale),(int)Math.Ceiling(element.ActualWidth*scale),(int)Math.Ceiling(element.ActualHeight*scale)));
        }
        // Keep caption rectangles disjoint from controls, including their padding.
        var gaps=new List<RectInt32>();int left=0;
        foreach(var rectangle in interactive.OrderBy(r=>r.X))
        {
            int start=Math.Clamp(rectangle.X,0,right),end=Math.Clamp(rectangle.X+rectangle.Width,0,right);
            if(start>left)gaps.Add(new(left,0,start-left,height));left=Math.Max(left,end);
        }
        if(left<right)gaps.Add(new(left,0,right-left,height));
        var nextCaption=gaps.ToArray();var nextPassthrough=interactive.ToArray();
        // Visibility changes can move the search box without a SizeChanged event.
        // Commit only settled geometry, and avoid native writes on every lyric frame.
        if(captionRegions.SequenceEqual(nextCaption)&&passthroughRegions.SequenceEqual(nextPassthrough))return;
        var source=InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        source.SetRegionRects(NonClientRegionKind.Passthrough,nextPassthrough);
        source.SetRegionRects(NonClientRegionKind.Caption,nextCaption);
        captionRegions=nextCaption;passthroughRegions=nextPassthrough;
    }
    private void ExitPlayer_Click(object sender,RoutedEventArgs args)=>Navigate("library");
}
