using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
using Windows.ApplicationModel.DataTransfer;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private long queueStructureRevision;
    private QueueUndo? queueUndo;
    private bool restoringQueue;
    private HashSet<Guid> playedQueueIds=[],removeAfterPlay=[];
    private int QueueAnchor=>Math.Max(index,queue.FindLastIndex(item=>playedQueueIds.Contains(item.ItemId)));
    private async Task RemoveQueueSelection(HashSet<Guid> selected,bool immediate)
    {
        if(!immediate){await EditQueue(()=>{if(index>=0&&selected.Contains(queue[index].ItemId))removeAfterPlay.Add(queue[index].ItemId);queue=QueueEditing.RemovePending(queue,QueueAnchor,selected).ToList();nextInsertion=index+1;});return;}
        await queueEdits.WaitAsync();try{if(busy||closed)return;busy=true;bool resume=audio.Status.State==2;try{
            if(resume&&!await Command(new("pause")))return;SynchronizeCurrent();var before=queue.ToArray();
            if(index>=0&&selected.Contains(queue[index].ItemId)){if(!await Command(new("stop")))return;int old=index;Guid removed=queue[old].ItemId;playedQueueIds.Add(removed);removeAfterPlay.Remove(removed);queue.RemoveAt(old);queue=QueueEditing.RemovePending(queue,old-1,selected).ToList();index=old<queue.Count?old:-1;if(index>=0){PresentTrack(queue[index].Track);waitingToResume=true;}else{current=null;presentedItemId=null;waitingToResume=false;SongTitle.Text=MiniTitle.Text="尚未选择歌曲";}resume=false;}
            else{if(audio.ContinuousCue&&!await Command(new("trim")))return;queue=QueueEditing.RemovePending(queue,QueueAnchor,selected).ToList();}
            if(!before.Select(i=>i.ItemId).SequenceEqual(queue.Select(i=>i.ItemId))){queueStructureRevision++;queueUndo=new(queueStructureRevision,before,index>=0?queue[index].ItemId:null,playedQueueIds.ToArray());ShowQueueUndo();}nextInsertion=index+1;SaveQueue(false);if(resume)await Command(new("play"));
        }finally{busy=false;}}finally{queueEdits.Release();}
    }
    private readonly DispatcherTimer undoToastTimer=new(){Interval=TimeSpan.FromSeconds(8)};
    private void ShowQueueUndo(){var button=new Button{Content="撤销"};button.Click+=async(_,_)=>await UndoQueue();Notice.ActionButton=button;Show("队列已更新 · 可撤销");undoToastTimer.Stop();undoToastTimer.Tick-=HideUndoToast;undoToastTimer.Tick+=HideUndoToast;undoToastTimer.Start();}
    private void HideUndoToast(object? sender,object e){undoToastTimer.Stop();Notice.ActionButton=null;}
    private async Task UndoQueue(){try{if(queueUndo==null)return;restoringQueue=true;await EditQueue(()=>{queue=QueueEditing.Restore(queue,QueueAnchor,queueStructureRevision,queueUndo,playedQueueIds).ToList();queueUndo=null;nextInsertion=index+1;});Notice.ActionButton=null;Show("已撤销待播队列更改");}catch(Exception ex){Show(ex.Message,true);}finally{restoringQueue=false;}}
    private void CompleteDeferred(Guid? previous)
    {
        if(previous==null)return;playedQueueIds.Add(previous.Value);
        if(removeAfterPlay.Remove(previous.Value)){int position=queue.FindIndex(i=>i.ItemId==previous);if(position>=0){queue.RemoveAt(position);if(position<=index)index--;nextInsertion=index+1;queueStructureRevision++;queueUndo=null;}}
    }
    private Task<bool> AllowQueueReorder(bool confirmed){if(!shuffle)return Task.FromResult(true);if(!confirmed){Show("请在队列中勾选允许关闭随机播放后，再调整顺序");return Task.FromResult(false);}shuffle=false;ShuffleButton.Opacity=.55;originalQueue=null;SaveQueue();return Task.FromResult(true);}
    private Func<QueueItem,bool,Task>? queueItemActivation;
    private Func<Track,Task>? queuePreviewStart=null;
    private async Task ActivateQueueItem(Guid itemId,Func<Track,Task>? startPreview=null)
    {
        await queueEdits.WaitAsync();
        try
        {
            if(busy||closed||!queue.Any(item=>item.ItemId==itemId))return;
            if(startPreview==null)
            {
                busy=true;
                try{if(audio.Status.State==2&&!await Command(new("pause")))return;SynchronizeCurrent();}
                finally{busy=false;}
            }
            Guid? previous=index>=0&&index<queue.Count?queue[index].ItemId:null;
            if(previous!=itemId)CompleteDeferred(previous);
            index=queue.FindIndex(item=>item.ItemId==itemId);
            if(index<0)return;
            playedQueueIds.Remove(itemId);removeAfterPlay.Remove(itemId);nextInsertion=index+1;
            if(startPreview!=null)await startPreview(queue[index].Track);
            else{clock.Start();await Start(queue[index].Track);}
        }
        finally{queueEdits.Release();}
    }
    private async void Queue_Click(object sender,RoutedEventArgs e)
    {
        var list=new ListView{Height=Math.Clamp(Root.ActualHeight-420,180,360),SelectionMode=ListViewSelectionMode.None,IsItemClickEnabled=true,CanDragItems=true,AllowDrop=true,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
        list.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
              <Grid Padding='0,7' ColumnSpacing='10'><Grid.ColumnDefinitions><ColumnDefinition Width='36'/><ColumnDefinition Width='*'/><ColumnDefinition Width='38'/><ColumnDefinition Width='16'/></Grid.ColumnDefinitions>
                <Border Width='36' Height='36' CornerRadius='8' Background='{ThemeResource LayerFillColorAltBrush}'><Grid><FontIcon Glyph='&#xE8D6;' FontSize='16' Opacity='.35'/><Image Stretch='UniformToFill'/>
                  <Border x:Name='CurrentQueueMarker' Visibility='Collapsed' Background='{ThemeResource ControlSolidFillColorDefaultBrush}' CornerRadius='5' Width='18' Height='18' HorizontalAlignment='Right' VerticalAlignment='Bottom'>
                    <StackPanel Orientation='Horizontal' Spacing='2' HorizontalAlignment='Center' VerticalAlignment='Center'><Border x:Name='WaveBar0' Width='2' Height='10' Background='{ThemeResource MuyuAccentBrush}'/><Border x:Name='WaveBar1' Width='2' Height='12' Background='{ThemeResource MuyuAccentBrush}'/><Border x:Name='WaveBar2' Width='2' Height='8' Background='{ThemeResource MuyuAccentBrush}'/></StackPanel>
                  </Border>
                </Grid></Border>
                <StackPanel Grid.Column='1' Spacing='3' VerticalAlignment='Center'><TextBlock Text='{Binding Track.DisplayTitle}' FontWeight='SemiBold' FontSize='14' TextTrimming='CharacterEllipsis'/><TextBlock Text='{Binding Track.Artist}' FontSize='11' Foreground='{ThemeResource TextFillColorSecondaryBrush}' TextTrimming='CharacterEllipsis'/></StackPanel>
                <TextBlock Grid.Column='2' Text='{Binding Track.DurationText}' FontSize='11' VerticalAlignment='Center' HorizontalAlignment='Right' Foreground='{ThemeResource TextFillColorSecondaryBrush}'/>
                <TextBlock x:Name='QueueDragHandle' Grid.Column='3' Text='⠿' Opacity='.12' FontSize='18' VerticalAlignment='Center'/>
              </Grid>
            </DataTemplate>
            """);
        list.ItemContainerStyle=(Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='CornerRadius' Value='12'/><Setter Property='Padding' Value='8,0'/></Style>");
        var waves=new Dictionary<FrameworkElement,PlaybackWave>();
        var hoverRows=new HashSet<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem>();
        bool queueVisible=true;
        bool AnimateWave()=>queueVisible&&(captureActive||audio.Status.State==2&&windowForeground)&&store.Get("motion")!="reduced"&&store.Get("glass-economy")!="true";
        async Task SetRowCover(Microsoft.UI.Xaml.Controls.Primitives.SelectorItem container,QueueItem item)
        {
            await Task.Yield();if(!queueVisible||!ReferenceEquals(container.Content,item))return;
            Image? FindImage(DependencyObject parent){if(parent is Image image)return image;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindImage(VisualTreeHelper.GetChild(parent,i));if(found!=null)return found;}return null;}
            FrameworkElement? FindNamed(DependencyObject parent,string name){if(parent is FrameworkElement element&&element.Name==name)return element;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var found=FindNamed(VisualTreeHelper.GetChild(parent,i),name);if(found!=null)return found;}return null;}
            if(FindNamed(container,"CurrentQueueMarker") is {} marker){marker.Visibility=index>=0&&index<queue.Count&&queue[index].ItemId==item.ItemId?Visibility.Visible:Visibility.Collapsed;if(!waves.TryGetValue(marker,out var wave)){wave=new(marker);waves.Add(marker,wave);}wave.Configure(AnimateWave());}
            if(hoverRows.Add(container)){container.PointerEntered+=(_,_)=>{if(FindNamed(container,"QueueDragHandle") is {} handle)handle.Opacity=.8;};container.PointerExited+=(_,_)=>{if(FindNamed(container,"QueueDragHandle") is {} handle)handle.Opacity=.12;};}
            var image=FindImage(container);if(image==null)return;
            var source=captureActive?CoverImage.Source:await Cover(item.Track);
            if(ReferenceEquals(container.Content,item))image.Source=source;
        }
        list.ContainerContentChanging+=(sender,args)=>{if(!args.InRecycleQueue&&args.Item is QueueItem item){args.ItemContainer.Opacity=1;Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(args.ItemContainer,(item.ItemId==(index>=0&&index<queue.Count?queue[index].ItemId:Guid.Empty)?"正在播放，":"")+item.Track.DisplayTitle+"，"+item.Track.Artist);_=SetRowCover(args.ItemContainer,item);}};
        var status=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=12,VerticalAlignment=VerticalAlignment.Center};
        var controls=new StackPanel{Orientation=Orientation.Horizontal,Spacing=6,Visibility=Visibility.Collapsed};var panel=new StackPanel{Spacing=12};
        var edit=new ToggleSwitch{Visibility=Visibility.Collapsed};panel.Children.Add(controls);
        var more=new Button{Content="···",Width=32,Height=32,MinHeight=32,Padding=new(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new(0)};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(more,"队列操作");
        var menu=new MenuFlyout();var select=new ToggleMenuFlyoutItem{Text="选择与排序"};select.Click+=(_,_)=>edit.IsOn=select.IsChecked;menu.Items.Add(select);
        var undo=new MenuFlyoutItem{Text="撤销队列更改"};undo.Click+=async(_,_)=>{await UndoQueue();};menu.Items.Add(undo);more.Flyout=menu;ApplyGlassToMenu(menu);
        var heading=new Grid{ColumnSpacing=12};heading.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});heading.ColumnDefinitions.Add(new(){Width=GridLength.Auto});heading.Children.Add(status);Grid.SetColumn(more,1);heading.Children.Add(more);
        panel.Children.Insert(0,heading);panel.Children.Insert(1,list);
        var removalPolicy=new ComboBox{Header="移除当前曲目时",ItemsSource=new[]{"播完后移除","立即停止并移除"},SelectedIndex=0,Visibility=Visibility.Collapsed};panel.Children.Add(removalPolicy);
        var disableShuffle=new CheckBox{Content="手动排序时关闭随机播放",IsChecked=false,Visibility=Visibility.Collapsed};panel.Children.Add(disableShuffle);
        edit.Toggled+=(_,_)=>{select.IsChecked=edit.IsOn;list.SelectionMode=edit.IsOn?ListViewSelectionMode.Extended:ListViewSelectionMode.None;controls.Visibility=removalPolicy.Visibility=edit.IsOn?Visibility.Visible:Visibility.Collapsed;disableShuffle.Visibility=edit.IsOn&&shuffle?Visibility.Visible:Visibility.Collapsed;};
        Guid[] Selection()=>list.SelectedItems.Cast<QueueItem>().Select(i=>i.ItemId).ToArray();
        Guid[] shown=[];int lastIndex=-2;
        void RefreshQueue(){var selection=Selection().ToHashSet();if(!shown.SequenceEqual(queue.Select(i=>i.ItemId))||lastIndex!=index){shown=queue.Select(i=>i.ItemId).ToArray();lastIndex=index;list.ItemsSource=queue.ToArray();if(edit.IsOn)foreach(var item in queue.Where(i=>selection.Contains(i.ItemId)))list.SelectedItems.Add(item);}status.Text=$"{queue.Count} 首 · 当前：{current?.Title??"未选择"}"+(removeAfterPlay.Count>0?"\n当前曲目将在播放结束后移除":"");}
        async Task Activate(QueueItem item,bool multiSelect)
        {
            if(edit.IsOn)return;
            if(multiSelect){edit.IsOn=true;list.SelectedItems.Add(item);return;}
            try{await ActivateQueueItem(item.ItemId,captureActive?queuePreviewStart:null);RefreshQueue();}
            catch(Exception ex){Show("切歌未完成："+ex.Message,true);}
        }
        queueItemActivation=Activate;
        list.ItemClick+=async(_,args)=>{if(args.ClickedItem is QueueItem item){bool Down(Windows.System.VirtualKey key)=>(Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)&Windows.UI.Core.CoreVirtualKeyStates.Down)!=0;await Activate(item,Down(Windows.System.VirtualKey.Control)||Down(Windows.System.VirtualKey.Shift));}};
        list.KeyDown+=async(_,args)=>{if(args.Key==Windows.System.VirtualKey.Enter&&list.SelectedItem is QueueItem item){args.Handled=true;try{await ActivateQueueItem(item.ItemId,captureActive?queuePreviewStart:null);RefreshQueue();}catch(Exception ex){Show(ex.Message,true);}}};
        void Button(string name,Func<Task> action){var b=new Button{Content=name,FontSize=12,Padding=new(10,6,10,6)};b.Click+=async(_,_)=>{try{await action();RefreshQueue();}catch(Exception ex){Show(ex.Message,true);}};controls.Children.Add(b);}
        Button("移除",async()=>await RemoveQueueSelection(Selection().ToHashSet(),removalPolicy.SelectedIndex==1));
        Button("清空待播",async()=>await EditQueue(()=>{queue=queue.Take(QueueAnchor+1).ToList();nextInsertion=index+1;}));Button("撤销",UndoQueue);
        foreach(int delta in new[]{-1,1})Button(delta<0?"上移":"下移",async()=>{var selected=Selection().ToHashSet();if(selected.Count==0||!await AllowQueueReorder(disableShuffle.IsChecked==true))return;await EditQueue(()=>{int first=queue.FindIndex(i=>selected.Contains(i.ItemId)),last=queue.FindLastIndex(i=>selected.Contains(i.ItemId));int destination=delta<0?first-1:last+2;if(destination<=QueueAnchor||destination>queue.Count)return;queue=QueueEditing.MovePending(queue,QueueAnchor,selected,destination).ToList();nextInsertion=index+1;});});
        list.DragItemsStarting+=(_,args)=>{var selected=args.Items.Cast<QueueItem>().Select(i=>i.ItemId).ToHashSet();if(queue.Take(QueueAnchor+1).Any(i=>selected.Contains(i.ItemId))){args.Cancel=true;return;}args.Data.SetText("muyu-queue:"+string.Join(',',selected));args.Data.RequestedOperation=DataPackageOperation.Move;};
        list.DragOver+=(_,args)=>{if(args.DataView.Contains(StandardDataFormats.Text))args.AcceptedOperation=DataPackageOperation.Move;};
        list.Drop+=async(_,args)=>{args.Handled=true;try{string value=await args.DataView.GetTextAsync();if(!value.StartsWith("muyu-queue:"))return;var selected=value[11..].Split(',').Select(Guid.Parse).ToHashSet();if(!selected.All(id=>queue.Any(i=>i.ItemId==id))||!await AllowQueueReorder(disableShuffle.IsChecked==true))return;var hit=args.OriginalSource as DependencyObject;while(hit!=null&&hit is not ListViewItem)hit=VisualTreeHelper.GetParent(hit);int destination=hit is ListViewItem item?list.IndexFromContainer(item):queue.Count;await EditQueue(()=>{queue=QueueEditing.MovePending(queue,QueueAnchor,selected,destination).ToList();nextInsertion=index+1;});RefreshQueue();}catch(Exception ex){Show(ex.Message,true);}};
        var clearPending=new Button{Content="清空待播",HorizontalAlignment=HorizontalAlignment.Stretch,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
        clearPending.Click+=async(_,_)=>{try{await EditQueue(()=>{queue=queue.Take(QueueAnchor+1).ToList();nextInsertion=index+1;});RefreshQueue();}catch(Exception error){Show(error.Message,true);}};panel.Children.Add(clearPending);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};timer.Tick+=(_,_)=>{RefreshQueue();foreach(var wave in waves.Values)wave.Configure(AnimateWave());};RefreshQueue();timer.Start();
        try{if(await ShowGlassPopover("播放队列",panel,440,"保存为播放列表"))await SaveQueuePlaylist();}finally{queueVisible=false;timer.Stop();foreach(var wave in waves.Values)wave.Dispose();if(queueItemActivation==Activate)queueItemActivation=null;}
    }
}
