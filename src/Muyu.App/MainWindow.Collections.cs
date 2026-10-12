using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private Dictionary<string,SmartPlaylist> smartPlaylists=[];
    private static readonly string[] SmartFieldNames=["格式","艺术家","专辑艺术家","专辑","流派","年份","采样率 Hz","有效位深","收藏（true/false）","播放次数","最近播放","添加时间","时长（秒）"];
    private static readonly string[] SmartOperatorNames=["等于","不等于","包含","大于","小于","最近几天","从未播放"];
    private static readonly string[] SmartSortNames=["标题","艺术家","专辑","添加时间","最近播放","播放次数","采样率","时长"];
    private void LoadSmartPlaylists(){var saved=Read<Dictionary<string,SmartPlaylist>>("smart-playlists")??[];foreach(var pair in saved){try{if(pair.Value==null)throw new FormatException();pair.Value.Validate();smartPlaylists[pair.Key]=pair.Value;}catch(FormatException){Save("smart-playlists-invalid",saved);sessionWarning="部分智能歌单规则损坏，原记录已保留，可重新创建";}}}
    private async Task<List<Track>> SortTracks(IEnumerable<Track> source)
    {
        var tracks=source.ToArray();int order=SortOrder?.SelectedIndex??0;
        if(order is 5 or 6)return await Task.Run(()=>TrackTimeSort.ByModified(tracks,order==5).ToList());
        if(order is 7 or 8)return await Task.Run(()=>TrackTimeSort.ByAdded(tracks,store.Usage(),order==7).ToList());
        return (order switch{1=>tracks.OrderBy(t=>t.Title).ThenBy(t=>t.Id),2=>tracks.OrderBy(t=>t.Artist).ThenBy(t=>t.Title),3=>tracks.OrderByDescending(t=>t.SampleRate).ThenBy(t=>t.Id),4=>tracks.OrderByDescending(t=>t.Duration).ThenBy(t=>t.Id),_=>tracks.OrderBy(t=>t.AlbumArtist).ThenBy(t=>t.Album).ThenBy(t=>t.Disc).ThenBy(t=>t.Number).ThenBy(t=>t.StartFrame).ThenBy(t=>t.Title)}).ToList();
    }
    private async void Sort_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(store==null||Root.XamlRoot==null)return;
        store.Set("library-sort",SortOrder.SelectedIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try{await Refresh();}catch(Exception ex){Show("无法更新排序："+ex.Message,true);}
    }
    private void CollectionActions_Click(object sender,RoutedEventArgs e)
    {
        var menu=new MenuFlyout();ApplyGlassToMenu(menu);
        void Action(string name,Func<Task> action){var item=new MenuFlyoutItem{Text=name};item.Click+=async(_,_)=>{try{await action();}catch(Exception ex){Show(ex.Message,true);}};menu.Items.Add(item);}
        Action("添加所选歌曲到队列",async()=>await EditQueue(()=>{foreach(var track in TrackList.SelectedItems.Cast<Track>())queue.Add(new(Guid.NewGuid(),track));}));
        Action("保存当前队列为歌单",SaveQueuePlaylist);
        Action("导入或切换 CUE 方案",ImportCueScheme);
        Action("从音乐库移除所选歌曲",async()=>{var selected=TrackList.SelectedItems.Cast<Track>().ToArray();if(selected.Length==0)return;if(await Dialog("移除音乐库索引",new TextBlock{Text=$"将移除 {selected.Length} 首的音乐库索引。源文件保留；现有歌单和队列保留可恢复引用。",TextWrapping=TextWrapping.Wrap},"移除索引")==ContentDialogResult.Primary){store.RemoveFromLibrary(selected.Select(t=>t.Id));await ReloadLibraryState();await Refresh();}});
        Action("导出当前列表为 Muyu 歌单",async()=>{var picker=new FileSavePicker{SuggestedFileName=PageTitle.Text};picker.FileTypeChoices.Add("Muyu 歌单",new List<string>{".muyu-playlist"});InitializeWithWindow.Initialize(picker,WindowNative.GetWindowHandle(this));var file=await picker.PickSaveFileAsync();if(file!=null){var snapshot=visible.ToArray();var evidence=store.Fingerprints(snapshot);string name=PageTitle.Text;await Task.Run(()=>PlaylistExchange.Save(file.Path,name,snapshot,evidence));Show($"已导出 {snapshot.Length} 项；包含片段和顺序");}});
        if(page=="smart"&&smartPlaylists.TryGetValue(PageTitle.Text,out var smart)){Action("编辑智能歌单",()=>EditSmart(smart));Action("删除智能歌单",async()=>{if(await Dialog("删除智能歌单",smart.Name,"删除")==ContentDialogResult.Primary){smartPlaylists.Remove(smart.Name);Save("smart-playlists",smartPlaylists);await Refresh();}});}
        menu.ShowAt((FrameworkElement)sender);
    }
    private async void ImportPlaylist_Click(object sender,RoutedEventArgs e)
    {
        try{var file=await Picker(".m3u",".m3u8",".muyu-playlist").PickSingleFileAsync();if(file!=null)await ImportPlaylistPath(file.Path);}catch(Exception ex){Show(ex.Message,true);}
    }
    private async Task ImportPlaylistPath(string filePath)
    {
        try
        {
            var file=new FileInfo(filePath);
            PlaylistImport result;
            try{result=await Task.Run(()=>PlaylistExchange.Import(file.FullName,NativeAudio.Probe));}
            catch(System.Text.DecoderFallbackException){var encoding=new ComboBox{Header="文件编码",ItemsSource=new[]{"GB18030","Shift-JIS","Big5","Windows-1252"},SelectedIndex=0};if(await Dialog("请选择歌单文本编码",encoding,"读取预览")!=ContentDialogResult.Primary)return;System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);result=await Task.Run(()=>PlaylistExchange.Import(file.FullName,NativeAudio.Probe,System.Text.Encoding.GetEncoding((string)encoding.SelectedItem,System.Text.EncoderFallback.ExceptionFallback,System.Text.DecoderFallback.ExceptionFallback)));}
            var name=new TextBox{Header="歌单名称",Text=Path.GetFileNameWithoutExtension(file.FullName)};
            var panel=new StackPanel{Spacing=12};panel.Children.Add(name);panel.Children.Add(new TextBlock{Text=$"可导入 {result.Tracks.Count} 项 · 重复项 {PlaylistSelection.Duplicates(result.Tracks)} 个\n警告 {result.Warnings.Count} 项",TextWrapping=TextWrapping.Wrap});panel.Children.Add(new ListView{Height=260,ItemsSource=result.Tracks.Select(t=>$"{t.Title} · {t.Artist}\n{t.Path} [{t.StartFrame}–{CuePlaybackRun.End(t)}]").Concat(result.Warnings).ToArray(),ItemTemplate=(DataTemplate)Root.Resources["WrappedTextItem"]});
            if(result.Tracks.Count==0){await Dialog("导入预览",panel);return;}
            if(await Dialog("导入歌单预览",panel,"导入")!=ContentDialogResult.Primary)return;
            string title=PlaylistSelection.ValidateName(name.Text,playlists.Keys);store.RememberSources(result.Tracks,result.Fingerprints!);var next=new Dictionary<string,List<string>>(playlists){[title]=result.Tracks.Select(t=>t.Id).ToList()};CommitPlaylists(next,result.Tracks);Show($"已导入「{title}」· {result.Tracks.Count} 项");Navigate("playlists");await Refresh();
        }catch(Exception ex){Show("歌单未导入："+ex.Message,true);}
    }
    private async void SmartPlaylist_Click(object sender,RoutedEventArgs e)=>await EditSmart(null);
    private async Task EditSmart(SmartPlaylist? original)
    {
        try
        {
            var name=new TextBox{Header="名称",Text=original?.Name??"",MaxLength=100};var mode=new ComboBox{Header="匹配",ItemsSource=new[]{"所有条件","任一条件"},SelectedIndex=original?.MatchAll==false?1:0};
            var rulePanel=new StackPanel{Spacing=10};var rows=new List<(ComboBox Field,ComboBox Op,TextBox Value,StackPanel Panel)>();
            void AddRule(SmartRule rule){var field=new ComboBox{Width=130,ItemsSource=SmartFieldNames,SelectedIndex=(int)rule.Field};var op=new ComboBox{Width=130,ItemsSource=SmartOperatorNames,SelectedIndex=(int)rule.Operator};var value=new TextBox{Width=110,Text=rule.Value,PlaceholderText="条件值"};var remove=new Button{Content="×"};var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6};foreach(var element in new UIElement[]{field,op,value,remove})row.Children.Add(element);rows.Add((field,op,value,row));rulePanel.Children.Add(row);remove.Click+=(_,_)=>{rows.RemoveAll(r=>r.Panel==row);rulePanel.Children.Remove(row);};}
            foreach(var rule in original?.Rules??[new(SmartField.Favorite,SmartOperator.Equals,"true")])AddRule(rule);
            var add=new Button{Content="添加条件"};add.Click+=(_,_)=>{if(rows.Count<32)AddRule(new(SmartField.Artist,SmartOperator.Contains,""));};
            var sort=new ComboBox{Header="排序",ItemsSource=SmartSortNames,SelectedIndex=(int)(original?.Sort??SmartSort.Album)};var descending=new CheckBox{Content="倒序",IsChecked=original?.Descending??false};var limit=new NumberBox{Header="最多曲目数（0 为不限）",Minimum=0,Maximum=100000,Value=original?.Limit??0};var text=new TextBlock{TextWrapping=TextWrapping.Wrap};var previewButton=new Button{Content="预览结果"};var previewRows=new ListView{Height=160};
            var panel=new StackPanel{Spacing=12};foreach(var element in new UIElement[]{name,mode,rulePanel,add,sort,descending,limit,previewButton,text,previewRows})panel.Children.Add(element);
            SmartPlaylist Build()=>new(original?.Id??Guid.NewGuid(),name.Text.Trim(),(original?.Revision??0)+1,mode.SelectedIndex==0,rows.Select(r=>new SmartRule((SmartField)r.Field.SelectedIndex,(SmartOperator)r.Op.SelectedIndex,r.Value.Text)).ToArray(),(SmartSort)sort.SelectedIndex,descending.IsChecked==true,(int)limit.Value);
            SmartPlaylist? frozen=null;SmartPreview? preview=null;
            previewButton.Click+=async(_,_)=>{try{var candidate=Build();candidate.Validate();long revision=store.LibraryRevision;var tracks=await Task.Run(()=>candidate.Evaluate(store.ReadAll(),favorites,store.Usage(),DateTimeOffset.Now).ToArray());if(revision!=store.LibraryRevision)throw new InvalidOperationException("音乐库已变化，请再次预览");frozen=candidate;preview=new(candidate.Id,candidate.Revision,revision,tracks);text.Text=$"{tracks.Length} 首";previewRows.ItemsSource=tracks.Select(t=>t.Title+" · "+t.Artist).ToArray();}catch(Exception ex){text.Text=ex.Message;preview=null;}};
            var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,RequestedTheme=EffectiveTheme,Title=original==null?"新建智能歌单":"编辑智能歌单",Content=new ScrollViewer{Content=panel,MaxHeight=510},PrimaryButtonText="保存",CloseButtonText="取消"};
            dialog.PrimaryButtonClick+=(_,args)=>{try{var currentRule=Build();currentRule.Validate();if(preview==null||frozen==null||System.Text.Json.JsonSerializer.Serialize(currentRule with{Id=frozen.Id})!=System.Text.Json.JsonSerializer.Serialize(frozen))throw new InvalidOperationException("请先预览当前规则");preview.ValidateCurrent(frozen,store.LibraryRevision);PlaylistSelection.ValidateName(currentRule.Name,smartPlaylists.Keys,original?.Name);if(original!=null)smartPlaylists.Remove(original.Name);smartPlaylists[currentRule.Name]=frozen;Save("smart-playlists",smartPlaylists);}catch(Exception ex){args.Cancel=true;text.Text=ex.Message;}};
            if(await dialog.ShowAsync()==ContentDialogResult.Primary){Navigate("smart");await Refresh();}
        }catch(Exception ex){Show(ex.Message,true);}
    }
}
