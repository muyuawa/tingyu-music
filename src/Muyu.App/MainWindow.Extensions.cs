using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using System.Text.Json;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private LyricsExtension[] lyricsExtensions=[];
    private void InitializeLyricsExtensions(){var saved=Read<LyricsExtension[]>("lyrics-extensions")??[];try{LyricsExtension.ValidateSet(saved);lyricsExtensions=saved;}catch(Exception ex)when(ex is FormatException or NullReferenceException){Save("lyrics-extensions-invalid",saved);Show("歌词扩展配置损坏，已保留原记录",true);}}
    private Func<LyricsExtension[]> LyricsExtensionEditor(StackPanel parent)
    {
        var draft=lyricsExtensions.ToList();var stack=new StackPanel{Spacing=10};var rows=new StackPanel{Spacing=10};var status=new TextBlock{Text="仅导入本地 JSON 文本读取规则；不会执行 DLL、脚本或联网。",TextWrapping=TextWrapping.Wrap};
        void RefreshReaders(){rows.Children.Clear();foreach(var reader in draft.ToArray()){var row=new StackPanel{Spacing=5};var enabled=new CheckBox{Content=reader.Name+" · "+string.Join(' ',reader.Extensions),IsChecked=reader.Enabled};enabled.Click+=(_,_)=>{int i=draft.FindIndex(e=>e.Id==reader.Id);if(i>=0)draft[i]=draft[i] with{Enabled=enabled.IsChecked==true};};var remove=new Button{Content="移除此扩展"};remove.Click+=(_,_)=>{draft.RemoveAll(e=>e.Id==reader.Id);RefreshReaders();};row.Children.Add(enabled);row.Children.Add(remove);rows.Children.Add(row);}}
        var import=new Button{Content="导入歌词扩展 JSON"};import.Click+=async(_,_)=>{try{var file=await Picker(".json").PickSingleFileAsync();if(file==null)return;if(new FileInfo(file.Path).Length>16384)throw new FormatException("扩展文件不能超过 16 KiB");var reader=JsonSerializer.Deserialize<LyricsExtension>(await File.ReadAllTextAsync(file.Path))??throw new FormatException("扩展文件为空");reader.Validate();draft.RemoveAll(r=>r.Id==reader.Id);draft.Add(reader with{Enabled=false});status.Text="已载入「"+reader.Name+"」；核对后勾选启用，保存设置才生效。";RefreshReaders();}catch(Exception ex){status.Text=ex.Message;}};stack.Children.Add(status);stack.Children.Add(import);stack.Children.Add(rows);RefreshReaders();parent.Children.Add(new Expander{Header="本地歌词扩展",Content=stack,HorizontalAlignment=HorizontalAlignment.Stretch});return ()=>{var result=draft.ToArray();LyricsExtension.ValidateSet(result);return result;};
    }
}
