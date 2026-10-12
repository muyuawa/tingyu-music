using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private GlassRenderer? glassRenderer;
    private FlowingBackdrop? flowingBackdrop;
    private TaskCompletionSource<bool>? popoverResult;
    private Control? popoverReturnFocus;
    private bool progressDragging;
    private double pendingSeek;
    private readonly DispatcherTimer seekDebounce=new(){Interval=TimeSpan.FromMilliseconds(120)};
    private void InitializeProgressInteraction()
    {
        Progress.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>progressDragging=true),true);
        Progress.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>{progressDragging=false;if(seekDebounce.IsEnabled)seekDebounce.Start();}),true);
        Progress.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler((_,_)=>{progressDragging=false;if(seekDebounce.IsEnabled)seekDebounce.Start();}),true);
        seekDebounce.Tick+=async(_,_)=>{if(progressDragging)return;seekDebounce.Stop();if(current!=null)await Seek(pendingSeek);};
    }

    private void InitializePlayerGlass()
    {
        glassRenderer = new GlassRenderer();
        flowingBackdrop=new FlowingBackdrop(AtmosphereHost);
        Activated+=(_,args)=>{windowForeground=args.WindowActivationState!=WindowActivationState.Deactivated;UpdateRenderActivity();};
        AppWindow.Changed+=(_,_)=>UpdateRenderActivity();
        InitializeVisualPolish();
        var parameters=Read<GlassParameters>("glass-settings")??new();try{parameters.Validate();}catch(FormatException){parameters=new();}
        if(store.Get("glass-style-revision")!="2")
        {
            var refreshed=parameters with{Blur=parameters.Blur is >0 and <=3?18:parameters.Blur,OverlayBlur=parameters.OverlayBlur is >0 and <=3?28:parameters.OverlayBlur};
            if(refreshed!=parameters){Save("glass-settings-before-0.11.1",parameters);Save("glass-settings",refreshed);parameters=refreshed;}
            store.Set("glass-style-revision","2");
        }
        glassRenderer.Parameters=parameters;
        glassRenderer.Failed += error =>
        {
            try{File.AppendAllText(Path.Combine(dataFolder,"glass-renderer.log"),DateTimeOffset.Now+"\n"+error+"\n");}catch(IOException){}catch(UnauthorizedAccessException){}
            Show("高级玻璃暂不可用，已切换为清晰面板；可在设置查看渲染状态",true);
        };
        glassRenderer.Configure(EffectiveDark,store.Get("transparency")=="reduced"||IsHighContrast,store.Get("motion")=="reduced",store.Get("glass-economy")=="true");
        glassRenderer.Attach(TransportSurface,GlassMaterial.Clear,46,brush=>{transportGlass=brush;ApplyTransportBackground();});
        glassRenderer.Attach(PlayButton,GlassMaterial.Button,22,brush=>PlayButton.Background=brush);
        glassRenderer.Attach(NavigationGlass,GlassMaterial.Navigation,22,brush=>NavigationGlass.Background=brush,NavigationHost);
        glassRenderer.Attach(FloatingSurface,GlassMaterial.Elevated,24,brush=>FloatingSurface.Background=brush);
        var playVisual=Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(PlayButton);
        PlayButton.SizeChanged+=(_,_)=>playVisual.CenterPoint=new((float)PlayButton.ActualWidth/2,(float)PlayButton.ActualHeight/2,0);
        void ScalePlay(float value){if(store.Get("motion")=="reduced")return;var animation=playVisual.Compositor.CreateVector3KeyFrameAnimation();animation.Duration=TimeSpan.FromMilliseconds(140);animation.InsertKeyFrame(1,new System.Numerics.Vector3(value));playVisual.StartAnimation("Scale",animation);animation.Dispose();}
        PlayButton.PointerEntered+=(_,_)=>ScalePlay(1.04f);PlayButton.PointerExited+=(_,_)=>ScalePlay(1);
        PlayButton.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>ScalePlay(.96f)),true);
        PlayButton.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>ScalePlay(1.04f)),true);
        PlayButton.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler((_,_)=>ScalePlay(1)),true);
        Navigation.SizeChanged+=(_,_)=>UpdateNavigationGlass();
        NowColumns.SizeChanged+=(_,_)=>Layout();
        ArtworkMetadata.SizeChanged+=(_,_)=>Layout();
        UpdateNavigationGlass();
        if(glassRenderer.Diagnostic!=null)Show("高级玻璃已降级；可在设置查看渲染状态",true);
    }

    private void UpdateNavigationGlass()
    {
        if(Navigation?.SelectedItem is not ListViewItem item||NavigationGlass==null||!item.IsLoaded)return;
        try
        {
            var point=item.TransformToVisual(NavigationHost).TransformPoint(new(0,0));
            NavigationGlass.Margin=new Thickness(0,Math.Max(0,point.Y-16),0,0);
            NavigationGlass.Height=Math.Max(36,item.ActualHeight);
        }catch(ArgumentException){}
    }

    private void ApplyTitleBarTheme(bool dark)
    {
        var bar=AppWindow.TitleBar;
        bar.ButtonBackgroundColor=bar.ButtonInactiveBackgroundColor=Colors.Transparent;
        bar.ButtonForegroundColor=dark?Color.FromArgb(255,244,247,251):Color.FromArgb(255,23,32,45);
        bar.ButtonHoverBackgroundColor=dark?Color.FromArgb(90,80,100,130):Color.FromArgb(90,205,220,240);
    }

    private Brush? transportGlass;
    private void ApplyTransportBackground()
    {
        TransportSurface.Background=transportGlass??new SolidColorBrush(Colors.Transparent);
        transportElevation?.SetVisible(store.Get("transparency")!="reduced"&&!IsHighContrast);
    }
    private void ResizeForDips(double width,double height,bool fitScreen=false)
    {
        double scale=GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this))/96.0;
        int pixelsWide=(int)Math.Ceiling(width*scale),pixelsHigh=(int)Math.Ceiling(height*scale);
        if(fitScreen){var work=Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;pixelsWide=Math.Min(pixelsWide,work.Width-32);pixelsHigh=Math.Min(pixelsHigh,work.Height-32);}
        AppWindow.Resize(new Windows.Graphics.SizeInt32(pixelsWide,pixelsHigh));
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")]private static extern uint GetDpiForWindow(IntPtr window);

    private Task<bool> ShowGlassPopover(string title,UIElement body,double width,string? primary=null)
    {
        ClosePopover();
        popoverReturnFocus=FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        popoverResult=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel=new StackPanel{Spacing=16};
        var header=new Grid();header.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        header.Children.Add(new TextBlock{Text=title,FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});
        var close=new Button{Content="×",Width=36,Height=36,Padding=new(0),Background=new SolidColorBrush(Colors.Transparent),BorderThickness=new(0)};
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(close,"关闭"+title);Grid.SetColumn(close,1);header.Children.Add(close);close.Click+=(_,_)=>ClosePopover();panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer{Content=body,MaxHeight=Math.Max(180,Root.ActualHeight-290),HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        if(primary!=null){var action=new Button{Content=primary,HorizontalAlignment=HorizontalAlignment.Stretch};action.Click+=(_,_)=>ClosePopover(true);panel.Children.Add(action);}
        FloatingSurface.Width=Math.Min(width,Math.Max(300,Root.ActualWidth-48));FloatingContent.Content=panel;
        PopoverLayer.Visibility=Visibility.Visible;
        glassRenderer?.Configure(EffectiveDark,store.Get("transparency")=="reduced"||IsHighContrast,store.Get("motion")=="reduced",store.Get("glass-economy")=="true");
        close.Focus(FocusState.Programmatic);
        return popoverResult.Task;
    }
    private void DismissPopover_Tapped(object sender,TappedRoutedEventArgs args){ClosePopover();args.Handled=true;}
    private void ClosePopover(bool primary=false)
    {
        if(PopoverLayer==null)return;
        PopoverLayer.Visibility=Visibility.Collapsed;
        var completion=popoverResult;popoverResult=null;completion?.TrySetResult(primary);
        popoverReturnFocus?.Focus(FocusState.Programmatic);popoverReturnFocus=null;
    }

    private void ApplyGlassToMenu(MenuFlyout menu)
    {
        FrameworkElement? presenter=null;
        menu.Opened+=(_,_)=>
        {
            foreach(var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if(popup.Child is MenuFlyoutPresenter found){presenter=found;glassRenderer?.Attach(found,GlassMaterial.Elevated,12,brush=>found.Background=brush);}
        };
        menu.Closed+=(_,_)=>{if(presenter!=null)glassRenderer?.Detach(presenter);presenter=null;};
    }
}
