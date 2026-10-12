using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Muyu.Core;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private ShortcutBinding[] keyBindings=Shortcuts.Defaults;
    private static readonly string[] ActionNames=["播放 / 暂停","打开音频文件","添加文件夹","搜索","撤销队列","上一首","下一首","停止","Mini 窗口","桌面歌词"];
    private void InitializeShortcuts(){var saved=Read<ShortcutBinding[]>("shortcuts");if(saved==null)return;try{Shortcuts.Validate(saved);keyBindings=saved;}catch(FormatException){Show("快捷键配置无效，已使用默认按键",true);}}
    private Func<ShortcutBinding[]> ShortcutEditor(StackPanel parent)
    {
        var editor=new StackPanel{Spacing=12};var rows=new List<(PlayerAction Action,TextBox Key,CheckBox Control,CheckBox Shift,CheckBox Alt)>();
        foreach(var binding in keyBindings.OrderBy(b=>b.Action)){var row=new StackPanel{Spacing=5};row.Children.Add(new TextBlock{Text=ActionNames[(int)binding.Action]});var key=new TextBox{Header="按键（Space、Left、Right、Enter、A–Z、F1–F12）",Text=((Windows.System.VirtualKey)binding.Key).ToString()};var modifiers=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};var ctrl=new CheckBox{Content="Ctrl",IsChecked=binding.Control};var shift=new CheckBox{Content="Shift",IsChecked=binding.Shift};var alt=new CheckBox{Content="Alt",IsChecked=binding.Alt};modifiers.Children.Add(ctrl);modifiers.Children.Add(shift);modifiers.Children.Add(alt);row.Children.Add(key);row.Children.Add(modifiers);editor.Children.Add(row);rows.Add((binding.Action,key,ctrl,shift,alt));}
        var reset=new Button{Content="恢复默认快捷键"};reset.Click+=(_,_)=>{foreach(var row in rows){var binding=Shortcuts.Defaults.First(b=>b.Action==row.Action);row.Key.Text=((Windows.System.VirtualKey)binding.Key).ToString();row.Control.IsChecked=binding.Control;row.Shift.IsChecked=binding.Shift;row.Alt.IsChecked=binding.Alt;}};editor.Children.Add(reset);parent.Children.Add(new Expander{Header="键盘快捷键",Content=editor,HorizontalAlignment=HorizontalAlignment.Stretch});
        return ()=>{var values=rows.Select(row=>new ShortcutBinding(row.Action,Enum.TryParse<Windows.System.VirtualKey>(row.Key.Text.Trim(),true,out var key)?(int)key:throw new FormatException("无法识别按键："+row.Key.Text),row.Control.IsChecked==true,row.Shift.IsChecked==true,row.Alt.IsChecked==true)).ToArray();Shortcuts.Validate(values);return values;};
    }
    private async void Track_DoubleTapped(object sender,DoubleTappedRoutedEventArgs e){var element=e.OriginalSource as FrameworkElement;while(element!=null&&element.DataContext is not Track)element=VisualTreeHelper.GetParent(element) as FrameworkElement;if(element?.DataContext is Track track&&!busy){ReplaceQueue(visible,visible.IndexOf(track));clock.Start();await Start(queue[index].Track,true);e.Handled=true;}}
    private readonly HashSet<Windows.System.VirtualKey> consumedShortcutKeys=[];
    private void Root_ShortcutReleased(object sender,KeyRoutedEventArgs e)
    {
        if(consumedShortcutKeys.Remove(e.Key))e.Handled=true;
    }
    private void Root_ShortcutPreview(object sender,KeyRoutedEventArgs e)
    {
        if(e.Handled)return;
        var root=(sender as FrameworkElement)?.XamlRoot??Root.XamlRoot;
        var focus=FocusManager.GetFocusedElement(root) as DependencyObject;
        bool textInput=false,modal=false,localControl=false;
        for(var node=focus;node!=null;node=VisualTreeHelper.GetParent(node))
        {
            modal|=node is ContentDialog;
            textInput|=node is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox;
            localControl|=node is ComboBox or Slider or ToggleSwitch or CheckBox or RadioButton or MenuFlyoutPresenter;
        }
        if(modal)return;
        if(e.Key==Windows.System.VirtualKey.Escape&&PopoverLayer.Visibility==Visibility.Visible){ClosePopover();e.Handled=true;return;}
        bool control=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool shift=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool alt=Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var binding=Shortcuts.Resolve(keyBindings,(int)e.Key,control,shift,alt,textInput,modal,localControl);
        if(binding!=null){
            // Consume before focused controls see either half of the keystroke.
            e.Handled=true;consumedShortcutKeys.Add(e.Key);
            if(e.KeyStatus.WasKeyDown)return;
            switch(binding.Action){case PlayerAction.PlayPause:Play_Click(this,new());break;case PlayerAction.OpenFiles:ImportFiles_Click(this,new());break;case PlayerAction.OpenFolder:ImportFolder_Click(this,new());break;case PlayerAction.Search:miniWindow?.Close();if(page=="now")Navigate("library");SearchBox.Focus(FocusState.Keyboard);break;case PlayerAction.UndoQueue:_=UndoQueue();break;case PlayerAction.Previous:Previous_Click(this,new());break;case PlayerAction.Next:Next_Click(this,new());break;case PlayerAction.Stop:Stop_Click(this,new());break;case PlayerAction.Mini:Mini_Click(this,new());break;case PlayerAction.DesktopLyrics:ToggleDesktopLyrics();break;}e.Handled=true;
        }
    }
    private void Root_Keyboard(object sender,KeyRoutedEventArgs e)
    {
        if(e.Handled||e.Key!=Windows.System.VirtualKey.Enter||e.KeyStatus.WasKeyDown)return;
        var focus=FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        bool inTrackList=false;
        for(var node=focus;node!=null;node=VisualTreeHelper.GetParent(node))
        {
            if(node is TextBox or PasswordBox or RichEditBox or ContentDialog)return;
            if(ReferenceEquals(node,TrackList)){inTrackList=true;break;}
        }
        bool modified=new[]{Windows.System.VirtualKey.Control,Windows.System.VirtualKey.Shift,Windows.System.VirtualKey.Menu}.Any(k=>Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down));
        if(inTrackList&&!modified&&TrackList.SelectedItem is Track track){e.Handled=true;ReplaceQueue(visible,visible.IndexOf(track));clock.Start();_=Start(track,true);}
    }
}
