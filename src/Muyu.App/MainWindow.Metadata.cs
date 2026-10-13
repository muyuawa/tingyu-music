using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using System.Text;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private async Task EditTags(Track track)
    {
        try
        {
            var panel=new StackPanel{Spacing=12};TextBox Text(string name,string value){var box=new TextBox{Header=name,Text=value};panel.Children.Add(box);return box;}NumberBox Number(string name,uint value){var box=new NumberBox{Header=name,Value=value,Minimum=0,Maximum=9999};panel.Children.Add(box);return box;}
            var title=Text("标题",track.Title);var artist=Text("艺术家（多值用分号分隔）",string.Join(';',track.Metadata?.Artists??[track.Artist]));var album=Text("专辑",track.Album);var albumArtist=Text("专辑艺术家",string.Join(';',track.Metadata?.AlbumArtists??[]));var genre=Text("流派",string.Join(';',track.Metadata?.Genres??[]));var year=Number("年份",track.Metadata?.Year??0);var disc=Number("碟号",track.Disc);var number=Number("音轨号",track.Number);panel.Children.Add(new TextBlock{Text="将写入原文件标签。写入前保存完整备份，并比较解码样本；样本不一致会回滚。",TextWrapping=TextWrapping.Wrap});
            if(await Dialog("编辑文件标签",new ScrollViewer{Content=panel,MaxHeight=500},"确认写入并保留备份")!=ContentDialogResult.Primary)return;
            if(current?.Path==track.Path){if(!await Command(new("pause")))return;CaptureReliablePosition();await Command(new("stop"));waitingToResume=true;SaveQueue(false);}
            var draft=new TagEditDraft(title.Text,artist.Text,album.Text,albumArtist.Text,genre.Text,(uint)year.Value,(uint)disc.Value,(uint)number.Value);
            var result=await Task.Run(()=>TagEditing.Apply(track,draft,store.AcceptTagEdit));sessionRevision=store.ReadSession().Session?.Revision??sessionRevision;await ReloadLibraryState();
            Track Updated(Track item)=>item.Path.Equals(result.Path,StringComparison.OrdinalIgnoreCase)?TagEditing.UpdateTrack(item,result.Metadata):item;
            queue=queue.Select(i=>i with{Track=Updated(i.Track)}).ToList();
            if(originalQueue!=null)originalQueue=originalQueue.Select(i=>i with{Track=Updated(i.Track)}).ToList();
            if(current!=null&&current.Path.Equals(result.Path,StringComparison.OrdinalIgnoreCase)){long frame=rememberedFrame;PresentTrack(Updated(current));waitingToResume=true;rememberedFrame=frame;resumeSeconds=(double)frame/current.SampleRate;UpdateStoredPosition();}covers.Clear();await Refresh();Show("标签已写入，原文件备份："+result.Backup);
        }catch(Exception ex){Show("标签未写入："+ex.Message,true);}
    }
    private async Task ImportCueScheme()
    {
        try
        {
            var file=await Picker(".cue").PickSingleFileAsync();if(file==null)return;
            var encoding=new ComboBox{Header="编码",ItemsSource=new[]{"UTF-8","GB18030","Shift-JIS","Big5","Windows-1252"},SelectedIndex=0};var pregap=new ComboBox{Header="INDEX 00 音频归属",ItemsSource=new[]{"归前一轨（默认）","归当前轨"},SelectedIndex=0};var hidden=new CheckBox{Content="显示首轨 INDEX 01 之前的隐藏音频"};var outside=new CheckBox{Content="允许此 CUE 引用同目录之外的本地文件"};var panel=new StackPanel{Spacing=12};foreach(var item in new UIElement[]{encoding,pregap,hidden,outside})panel.Children.Add(item);
            if(await Dialog("CUE 读取选项",panel,"读取预览")!=ContentDialogResult.Primary)return;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var selectedEncoding=Encoding.GetEncoding((string)encoding.SelectedItem,EncoderFallback.ExceptionFallback,DecoderFallback.ExceptionFallback);bool allowOutside=outside.IsChecked==true,toNext=pregap.SelectedIndex==1,showHidden=hidden.IsChecked==true;
            var fingerprints=new Dictionary<string,SourceFingerprint>(StringComparer.OrdinalIgnoreCase);
            var tracks=await Task.Run(()=>CueParser.Load(file.Path,path=>{if(!allowOutside&&!LibraryScope.Contains(Path.GetDirectoryName(file.Path)!,path,false))throw new IOException("引用超出当前目录，请明确允许外部引用后重试");if(LibraryScope.HasLink(path))throw new IOException("不读取链接来源");using var guard=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);fingerprints[path]=LocalFileIdentity.Capture(path);return NativeAudio.Probe(path);},selectedEncoding,toNext,showHidden));
            var sources=tracks.Select(t=>t.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);var old=library.Where(t=>sources.Contains(t.Path)).ToArray();
            var preview=new StackPanel{Spacing=12};preview.Children.Add(new TextBlock{Text=$"新方案 {tracks.Count} 轨 · 旧方案 {old.Length} 项\n切换后旧方案退出音乐库；现有队列和歌单保留旧样本范围。重叠来源只保留新方案。PREGAP 声明不额外生成静音。",TextWrapping=TextWrapping.Wrap});preview.Children.Add(new ListView{Height=280,ItemsSource=tracks.Select(t=>$"{t.Title} · {t.DurationText}\n{t.Path}\n样本 [{t.StartFrame}, {CuePlaybackRun.End(t)})").ToArray(),ItemTemplate=(DataTemplate)Root.Resources["WrappedTextItem"]});
            if(await Dialog("CUE 方案切换预览",preview,"切换到此方案")!=ContentDialogResult.Primary)return;
            await Task.Run(()=>{foreach(var pair in fingerprints)if(LocalFileIdentity.Capture(pair.Key)!=pair.Value)throw new IOException("CUE 来源在预览后变化，请重新预览");store.AddBatch(tracks.ToArray(),fingerprints:fingerprints);});await ReloadLibraryState();await Refresh();Show("已切换 CUE 方案；旧引用保留原来的播放范围");
        }catch(Exception ex){Show("CUE 方案未切换："+ex.Message,true);}
    }
}
