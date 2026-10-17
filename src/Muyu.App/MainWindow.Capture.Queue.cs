using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;

namespace Muyu.App;
public sealed partial class MainWindow
{
    // Runs inside the diagnostic app with isolated data; no mouse/keyboard injection or screenshots.
    private async Task VerifyQueueScrolling(string output)
    {
        captureActive=false;clock.Stop();
        var checks=new List<object>();bool passed=true,completed=false;
        void Check(bool ok,string name,object? detail=null){passed&=ok;checks.Add(new{name,passed=ok,detail});}
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);yield return child;
                foreach(var descendant in Descendants(child))yield return descendant;
            }
        }
        var tracks=new List<Track>();
        for(int i=0;i<3;i++)
        {
            string directory=Path.Combine(output,"fixtures",i.ToString());Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"track.wav");
            using(var writer=new BinaryWriter(File.Create(path)))
            {
                writer.Write("RIFF"u8);writer.Write(40);writer.Write("WAVEfmt "u8);writer.Write(16);
                writer.Write((short)1);writer.Write((short)2);writer.Write(44100);writer.Write(176400);
                writer.Write((short)4);writer.Write((short)16);writer.Write("data"u8);writer.Write(4);writer.Write(0);
            }
            if(i<2)File.Copy(Path.Combine(AppContext.BaseDirectory,"Assets","app.png"),Path.Combine(directory,"cover.png"),true);
            tracks.Add(new("queue-test-"+i,path,"Queue fixture "+i,"Test","Test",44100,16,2,44100));
        }
        queue=Enumerable.Range(0,403).Select(i=>new QueueItem(Guid.NewGuid(),tracks[i%3])).ToList();
        index=0;current=tracks[0];
        try
        {
            var expectedCovers=new List<ImageSource?>();
            foreach(var track in tracks)expectedCovers.Add(await Cover(track));
            Check(expectedCovers[0]!=null&&expectedCovers[1]!=null&&expectedCovers[2]==null,"Cover fixtures load before recycling");
            foreach(var size in new[]{(Width:1440,Height:940),(Width:1000,Height:720),(Width:760,Height:620)})
            {
                shuffle=size.Height==620;
                ResizeForDips(size.Width,size.Height);await Task.Delay(200);
                Queue_Click(this,new());await Task.Delay(500);Root.UpdateLayout();
                var list=Descendants(FloatingContent).OfType<ListView>().Single();
                var scroll=Descendants(list).OfType<ScrollViewer>().First();
                var parents=new List<ScrollViewer>();
                for(var parent=VisualTreeHelper.GetParent(list);parent!=null&&!ReferenceEquals(parent,Root);parent=VisualTreeHelper.GetParent(parent))
                    if(parent is ScrollViewer outer)parents.Add(outer);
                Check(parents.Count==0,"Queue has one scrolling owner at "+size.Height,new{parents=parents.Select(p=>new{p.Name,p.ScrollableHeight,p.ActualHeight}).ToArray(),scroll.ScrollableHeight});
                var scrollbar=Descendants(scroll).OfType<ScrollBar>().First(b=>b.Orientation==Orientation.Vertical);
                // Select the control's mouse presentation directly, without synthesizing input.
                VisualStateManager.GoToState(scroll,"MouseIndicatorFull",false);
                scrollbar.IndicatorMode=ScrollingIndicatorMode.MouseIndicator;
                VisualStateManager.GoToState(scrollbar,"ExpandedWithoutAnimation",false);
                await Task.Delay(500);
                var thumb=Descendants(scrollbar).OfType<Thumb>().Single(t=>t.Name=="VerticalThumb");
                var location=thumb.TransformToVisual(Root).TransformPoint(new(thumb.ActualWidth/2,thumb.ActualHeight/2));
                // The root is disabled by the general capture harness; temporarily allow hit testing.
                Root.IsHitTestVisible=true;
                var hits=VisualTreeHelper.FindElementsInHostCoordinates(location,Root).ToArray();
                bool thumbReachable=false;
                for(DependencyObject? hit=hits.FirstOrDefault();hit!=null;hit=VisualTreeHelper.GetParent(hit))
                    if(ReferenceEquals(hit,thumb)){thumbReachable=true;break;}
                Check(thumbReachable,"Scrollbar thumb receives foremost pointer hit at "+size.Height,new{thumb.ActualWidth,thumb.ActualHeight,location.X,location.Y,hits=hits.Select(h=>h.GetType().Name+":"+(h as FrameworkElement)?.Name).ToArray()});
                foreach(double fraction in new[]{0,.85,.2,1,.05})
                {
                    double target=scroll.ScrollableHeight*fraction;
                    scroll.ChangeView(null,target,null,true);await Task.Delay(250);Root.UpdateLayout();
                    int visible=0,missing=0,stale=0;
                    for(int i=0;i<queue.Count;i++)
                    {
                        if(list.ContainerFromIndex(i) is not ListViewItem container||container.ContentTemplateRoot==null)continue;
                        var top=container.TransformToVisual(list).TransformPoint(new(0,0)).Y;
                        if(top+container.ActualHeight<=0||top>=list.ActualHeight)continue;
                        visible++;
                        var image=Descendants(container).OfType<Image>().FirstOrDefault();
                        var expected=expectedCovers[i%3];
                        if(expected!=null&&image?.Source==null)missing++;
                        else if(!ReferenceEquals(expected,image?.Source))stale++;
                    }
                    Check(visible>0&&missing==0&&stale==0,"Recycled covers at "+size.Height+" / "+fraction,new{visible,missing,stale});
                    // A virtualizing panel refines its estimated extent as rows are measured.
                    double expectedOffset=fraction==1?scroll.ScrollableHeight:target;
                    Check(Math.Abs(scroll.VerticalOffset-expectedOffset)<(fraction is 0 or 1?2:60),"Queue scroll offset at "+size.Height+" / "+fraction,new{target,scroll.VerticalOffset,scroll.ScrollableHeight});
                }
                await queueItemActivation!(queue[5],true);Root.UpdateLayout();await Task.Delay(150);
                var surfaceTop=FloatingSurface.TransformToVisual(Root).TransformPoint(new(0,0)).Y;
                var listTop=list.TransformToVisual(Root).TransformPoint(new(0,0)).Y;
                var footer=Descendants(FloatingContent).OfType<Button>().Last(b=>Equals(b.Content,"清空待播"));
                var footerTop=footer.TransformToVisual(Root).TransformPoint(new(0,0)).Y;
                var primary=Descendants(FloatingContent).OfType<Button>().Single(b=>Equals(b.Content,"保存为播放列表"));
                var primaryTop=primary.TransformToVisual(Root).TransformPoint(new(0,0)).Y;
                Check(list.SelectionMode==ListViewSelectionMode.Extended&&list.ActualHeight>=80,"Edit mode retains a usable queue viewport at "+size.Height);
                Check(surfaceTop>=0&&listTop+list.ActualHeight<=footerTop&&footerTop+footer.ActualHeight<=primaryTop&&primaryTop+primary.ActualHeight<=Root.ActualHeight,"Edit controls and footer fit at "+size.Height,new{surfaceTop,listTop,list.ActualHeight,footerTop,primaryTop});
                ClosePopover();await Task.Delay(100);
            }
            completed=true;
        }
        finally
        {
            ClosePopover();
            await File.WriteAllTextAsync(Path.Combine(output,"QUEUE_VERIFICATION.json"),JsonSerializer.Serialize(new{passed=passed&&completed,completed,checks,audio_opened=false,input_injected=false},new JsonSerializerOptions{WriteIndented=true}));
        }
        if(!passed)throw new InvalidOperationException("Queue scrolling/cover regression failed");
    }
}
