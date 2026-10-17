using Microsoft.UI.Windowing;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private bool MainSurfaceVisible=>AppWindow.IsVisible&&AppWindow.Presenter is not OverlappedPresenter{State:OverlappedPresenterState.Minimized};
    private void UpdateRenderActivity()
    {
        if(closed)return;
        bool visible=MainSurfaceVisible;
        LyricsCanvas.SetRenderingActive(visible&&page=="now");
        flowingBackdrop?.SetForeground(visible&&windowForeground);
        var interval=TimeSpan.FromMilliseconds(!visible?1000:windowForeground&&page=="now"?100:250);
        if(clock.Interval!=interval)clock.Interval=interval;
    }
}
