using System.Text.Json;
using Microsoft.UI.Xaml;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureCaptionTransitions(string output)
    {
        var checks=new List<object>();bool pass=true;
        foreach(int width in new[]{1440,1000,780})
        {
            ResizeForDips(width,820);await Task.Delay(200);
            for(int cycle=0;cycle<4;cycle++)
            {
                Navigate("now");await Refresh();await Task.Delay(120);
                var back=ExitPlayerButton.TransformToVisual(Root).TransformPoint(new(20,20));
                int backHit=CaptureTitleBarHitTest(back.X,back.Y,false);
                ExitPlayer_Click(this,new());await Refresh();Navigate("home");await Refresh();await Task.Delay(120);
                var search=SearchBox.TransformToVisual(Root).TransformPoint(new(0,0));
                double gapX=Math.Max(16,search.X-20);
                int gap=CaptureTitleBarHitTest(gapX,24,false);
                int brand=CaptureTitleBarHitTest(30,24,false);
                int searchHit=CaptureTitleBarHitTest(search.X+20,search.Y+16,false);
                bool ok=gap==2&&brand==2&&searchHit==1&&backHit==1;pass&=ok;
                checks.Add(new{width,cycle,gap_x=gapX,left_of_search=gap,brand,search=searchHit,back=backHit,pass=ok});
            }
        }
        await WindowCapture.Save(this,Path.Combine(output,"home-after-lyrics.png"));
        await File.WriteAllTextAsync(Path.Combine(output,"CAPTION_VERIFICATION.json"),JsonSerializer.Serialize(new{pass,checks,hit_test_does_not_refresh_regions=true,dpi_scale=Root.XamlRoot.RasterizationScale,application_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))},new JsonSerializerOptions{WriteIndented=true}));
        if(!pass)throw new InvalidOperationException("Caption transitions failed");
    }
}
