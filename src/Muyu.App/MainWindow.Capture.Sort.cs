using System.Text.Json;
using Muyu.Core;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task CaptureTrackSorting(string output)
    {
        string first=Path.Combine(output,"first.fixture"),second=Path.Combine(output,"second.fixture");
        await File.WriteAllTextAsync(first,"Sorting fixture; not audio");await File.WriteAllTextAsync(second,"Sorting fixture; not audio");
        File.SetLastWriteTimeUtc(first,new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(second,new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        var a=new Track("sort-a",first,"先添加 · 文件较新","测试","排序验证",44100,16,2,441000);
        var b=new Track("sort-b",second,"后添加 · 文件较旧","测试","排序验证",44100,16,2,441000);
        store.AddBatch([a]);await Task.Delay(25);store.AddBatch([b]);library=[a,b];
        Navigate("library");await Refresh();var results=new List<object>();bool passed=true;
        foreach(int order in new[]{5,6,7,8})
        {
            SortOrder.SelectedIndex=order;await Refresh();await Task.Delay(150);
            var expected=order is 5 or 8?new[]{a.Id,b.Id}:new[]{b.Id,a.Id};
            bool ok=visible.Select(t=>t.Id).SequenceEqual(expected)&&store.Get("library-sort")==order.ToString();passed&=ok;
            results.Add(new{order,label=SortOrder.SelectedItem?.ToString(),actual=visible.Select(t=>t.Id).ToArray(),expected,passed=ok});
        }
        SortOrder.IsDropDownOpen=true;await Task.Delay(250);
        await WindowCapture.Save(this,Path.Combine(output,"time-sort-options.png"));SortOrder.IsDropDownOpen=false;
        await File.WriteAllTextAsync(Path.Combine(output,"SORT_VERIFICATION.json"),JsonSerializer.Serialize(new{passed,checks=results,audio_opened=false,application_sha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(MainWindow).Assembly.Location)))},new JsonSerializerOptions{WriteIndented=true}));
        if(!passed)throw new InvalidOperationException("Time sorting UI verification failed");
    }
}
