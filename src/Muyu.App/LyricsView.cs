using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Muyu.Core;
using Windows.Foundation;

namespace Muyu.App;

public sealed class LyricsView : UserControl
{
    private AlbumScene? colorScene;
    public void SetColorScene(AlbumScene? scene){if(colorScene==scene)return;colorScene=scene;ClearEffects();canvas.Invalidate();}
    private readonly CanvasControl canvas = new();
    private readonly ListView readable = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = true, Visibility = Visibility.Collapsed };
    private readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer browseTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Button follow = new() { Content = "回到当前", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 8, 8) };
    private readonly List<(CanvasTextLayout Layout, float Y, float Height)> layouts = [];
    private Lyrics lyrics = Lyrics.Empty;
    private int active = -1, hover = -1;
    private float offset, target, totalHeight, scrollFrom;
    private DateTime scrollStarted;
    private bool scrolling;
    private bool renderingActive=true;
    public void SetRenderingActive(bool value)
    {
        if(renderingActive==value)return;renderingActive=value;
        if(!value){animation.Stop();browseTimer.Stop();scrolling=false;ClearEffects();}
        else{browseTimer.Start();Follow(true);canvas.Invalidate();}
    }
    private bool browsing, pointerInside, persistentClear, playing;
    private DateTime lastBrowse, hoverStarted;
    private int pendingHover = -1, previousActive = -1;
    private DateTime focusChanged;
    private double lyricTime;
    private readonly Dictionary<(int Line,int Sigma,byte Alpha),(CanvasCommandList Image,GaussianBlurEffect Blur)> effectCache=[];
    private void ClearEffects(){foreach(var value in effectCache.Values){value.Blur.Dispose();value.Image.Dispose();}effectCache.Clear();}
    public bool BlurEnabled { get; set; } = true;
    public bool ReduceMotion { get; set; }
    private double bottomInset;
    public double BottomInset
    {
        get=>bottomInset;
        set
        {
            if(!double.IsFinite(value)||Math.Abs(bottomInset-value)<.1)return;
            bottomInset=Math.Max(0,value);
            follow.Margin=new Thickness(0,0,8,bottomInset+8);
            readable.Padding=new Thickness(0,0,0,bottomInset+16);
            Rebuild();
        }
    }
    private float FocusHeight=>Math.Max(80,(float)(canvas.ActualHeight-bottomInset));
    public Func<double, bool>? CanSeek { get; set; }
    public event Action<double>? SeekRequested;
    public event Action? FollowingResumed;
    internal float FadeEnd=>(float)canvas.ActualHeight;
    public LyricsView()
    {
        var grid = new Grid(); grid.Children.Add(canvas); grid.Children.Add(readable); grid.Children.Add(follow); Content = grid;
        follow.Click += (_, _) => ResumeFollowing();
        IsTabStop = true; AutomationProperties.SetName(this, "同步歌词");
        canvas.Draw += Draw;
        canvas.CreateResources += (_, _) => Rebuild();
        canvas.SizeChanged += (_, _) => Rebuild();
        canvas.PointerMoved += (_, e) => { pointerInside = true; int hit = Hit(e.GetCurrentPoint(canvas).Position.Y); if (pendingHover != hit) { pendingHover = hit; hoverStarted = DateTime.UtcNow; animation.Start(); } };
        canvas.PointerExited += (_, _) => { pointerInside = false; pendingHover = hover = -1; lastBrowse = DateTime.UtcNow; canvas.Invalidate(); };
        canvas.PointerWheelChanged += (_, e) => { browsing = true; scrolling = false; lastBrowse = DateTime.UtcNow; follow.Visibility = Visibility.Visible; offset = Math.Clamp(offset - e.GetCurrentPoint(canvas).Properties.MouseWheelDelta * .65f, 0, Math.Max(0, totalHeight - (float)canvas.ActualHeight)); target = offset; canvas.Invalidate(); e.Handled = true; };
        canvas.PointerPressed += (_, e) => { int hit = Hit(e.GetCurrentPoint(canvas).Position.Y); if (hit >= 0 && lyrics.Timed && CanSeek?.Invoke(lyrics.Lines[hit].Time) != false) { ResumeFollowing(); SeekRequested?.Invoke(lyrics.Lines[hit].Time); } };
        canvas.PointerMoved += (_, e) => { int hit = Hit(e.GetCurrentPoint(canvas).Position.Y); ToolTipService.SetToolTip(canvas, hit >= 0 && lyrics.Timed && CanSeek?.Invoke(lyrics.Lines[hit].Time) == false ? "此行在当前曲目范围之外，仅供浏览" : null); };
        readable.ItemClick += (_, e) => { if (lyrics.Timed && e.ClickedItem is LyricLine line && CanSeek?.Invoke(line.Time) != false) { ResumeFollowing(); SeekRequested?.Invoke(line.Time); } };
        readable.ContainerContentChanging += (_, e) => { if (e.Item is LyricLine line) ToolTipService.SetToolTip(e.ItemContainer, lyrics.Timed && CanSeek?.Invoke(line.Time) == false ? "此行在当前曲目范围之外，仅供浏览" : null); };
        readable.ItemTemplate=(DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><TextBlock Text='{Binding Text}' TextWrapping='Wrap' IsTextSelectionEnabled='True' Margin='12,10' FontSize='23'/></DataTemplate>");
        readable.ContainerContentChanging+=(_,args)=>{if(args.Item is LyricLine line)AutomationProperties.SetName(args.ItemContainer,(lyrics.Timed?TimeSpan.FromSeconds(line.Time).ToString(@"m\:ss")+" ":"")+line.Text);};
        RightTapped+=(_,e)=>{var menu=new MenuFlyout();var copy=new MenuFlyoutItem{Text="复制全部歌词"};copy.Click+=(_,_)=>{var data=new Windows.ApplicationModel.DataTransfer.DataPackage();data.SetText(string.Join("\n",lyrics.Lines.Select(l=>l.Text)));Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);};menu.Items.Add(copy);var read=new MenuFlyoutItem{Text="阅读与选择文字"};read.Click+=(_,_)=>SetClear(true);menu.Items.Add(read);menu.ShowAt(this);e.Handled=true;};
        animation.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            if (scrolling)
            {
                float progress = Math.Clamp((float)(now - scrollStarted).TotalMilliseconds / 240, 0, 1);
                offset = scrollFrom + (target - scrollFrom) * (1 - MathF.Pow(1 - progress, 3));
                if (progress >= 1) { offset = target; scrolling = false; }
            }
            if ((now - hoverStarted).TotalMilliseconds >= 80) hover = pendingHover;
            if (!scrolling && (now - focusChanged).TotalMilliseconds >= 180 && hover == pendingHover) animation.Stop();
            canvas.Invalidate();
        };
        browseTimer.Tick += (_, _) => { if (browsing && !persistentClear && playing && !pointerInside && FocusState == FocusState.Unfocused && (DateTime.UtcNow - lastBrowse).TotalSeconds >= 6) { browsing = false; follow.Visibility = Visibility.Collapsed; Follow(); } };
        Loaded += (_, _) => {if(renderingActive)browseTimer.Start();};
        Unloaded += (_, _) => { animation.Stop(); browseTimer.Stop(); ClearEffects(); };
        KeyDown += Keyboard;
        ActualThemeChanged += (_, _) => {ClearEffects();canvas.Invalidate();};
    }
    public void Load(Lyrics value)
    {
        animation.Stop(); scrolling = false;
        lyrics = value; active = previousActive = -1; offset = target = 0; browsing = false; follow.Visibility = Visibility.Collapsed;
        readable.ItemsSource = lyrics.Lines;
        Rebuild();
    }
    public void SetClear(bool clear)
    {
        persistentClear = clear;
        readable.Visibility = clear ? Visibility.Visible : Visibility.Collapsed;
        canvas.Visibility = clear ? Visibility.Collapsed : Visibility.Visible;
        browsing = clear; follow.Visibility = clear && lyrics.Timed ? Visibility.Visible : Visibility.Collapsed; if (!clear) Follow();
    }
    public void ResumeFollowing(bool immediately = false)
    {
        if (!lyrics.Timed) return;
        SetClear(false);
        hover = pendingHover = -1;
        Follow(immediately);
        FollowingResumed?.Invoke();
        canvas.Invalidate();
    }
    public void SetPlaying(bool value) { if (playing != value) lastBrowse = DateTime.UtcNow; playing = value; }
    public void Update(double time, double userOffset)
    {
        lyricTime=time-lyrics.FileOffset-userOffset;
        int next = lyrics.ActiveIndex(time, userOffset);
        if (next == active) {if(renderingActive&&active>=0&&lyrics.Lines[active].Words?.Count>0)canvas.Invalidate();return;}
        previousActive = active; active = next; focusChanged = DateTime.UtcNow;
        AutomationProperties.SetName(this, active >= 0 ? "当前歌词：" + lyrics.Lines[active].Text : "同步歌词");
        if (!browsing) Follow();
        if (renderingActive&&!ReduceMotion) animation.Start();
        canvas.Invalidate();
    }
    private void Keyboard(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Home) { ResumeFollowing(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Up || e.Key == Windows.System.VirtualKey.Down)
        {
            browsing = true; scrolling = false; lastBrowse = DateTime.UtcNow; follow.Visibility = Visibility.Visible; hover = Math.Clamp((hover < 0 ? Math.Max(active, 0) : hover) + (e.Key == Windows.System.VirtualKey.Down ? 1 : -1), 0, Math.Max(0, layouts.Count - 1)); pendingHover = hover;
            if (hover < layouts.Count) { offset = target = Math.Max(0, layouts[hover].Y - FocusHeight / 2); AutomationProperties.SetName(this, lyrics.Lines[hover].Text); }
            canvas.Invalidate(); e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter && hover >= 0 && hover < lyrics.Lines.Count && lyrics.Timed && CanSeek?.Invoke(lyrics.Lines[hover].Time) != false) { double time = lyrics.Lines[hover].Time; ResumeFollowing(); SeekRequested?.Invoke(time); e.Handled = true; }
    }
    private int Hit(double y)
    {
        for (int i = 0; i < layouts.Count; i++) if (y + offset >= layouts[i].Y && y + offset < layouts[i].Y + layouts[i].Height) return i;
        return -1;
    }
    private void Rebuild()
    {
        if (!canvas.ReadyToDraw || canvas.ActualWidth < 40) return;
        ClearEffects();foreach (var item in layouts) item.Layout.Dispose(); layouts.Clear();
        float y = FocusHeight * .35f;
        using var format = new CanvasTextFormat { FontFamily = "Segoe UI Variable, Microsoft YaHei UI", FontSize = canvas.ActualWidth<430?24:32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, WordWrapping = CanvasWordWrapping.Wrap };
        foreach (var line in lyrics.Lines)
        {
            var layout = new CanvasTextLayout(canvas, string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text, format, Math.Max(20, (float)canvas.ActualWidth - 48), 0);
            float height = Math.Max(40, (float)layout.LayoutBounds.Height) + 40; layouts.Add((layout, y, height)); y += height;
        }
        totalHeight = y + FocusHeight * .45f + (float)bottomInset; Follow(); canvas.Invalidate();
    }
    private void Follow(bool immediately = false)
    {
        if (active >= layouts.Count || browsing) return;
        float next = active < 0 ? 0 : Math.Max(0, layouts[active].Y - FocusHeight * .42f);
        if (!renderingActive || immediately || ReduceMotion || Math.Abs(offset - next) > canvas.ActualHeight * 1.5)
        { target = offset = next; scrolling = false; animation.Stop(); canvas.Invalidate(); }
        else if (Math.Abs(next - target) > .1f || (!scrolling && Math.Abs(offset - next) > .1f))
        { scrollFrom = offset; target = next; scrollStarted = DateTime.UtcNow; scrolling = true; animation.Start(); }
    }
    private void Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if(!renderingActive)return;
        // Focus reserves control space; the drawing viewport continues behind the glass.
        float height = Math.Max(1, FadeEnd);
        using var mask = new CanvasLinearGradientBrush(sender, new CanvasGradientStop[]
        {
            new() { Position = 0, Color = Colors.Transparent },
            new() { Position = Math.Min(.22f, 60 / height), Color = Colors.White },
            new() { Position = Math.Max(.78f, 1 - 80 / height), Color = Colors.White },
            new() { Position = 1, Color = Colors.Transparent }
        }) { StartPoint = Vector2.Zero, EndPoint = new Vector2(0, height) };
        using var fadeLayer = args.DrawingSession.CreateLayer(mask);
        var color = colorScene is {} scene?Windows.UI.Color.FromArgb(255,scene.Text.R,scene.Text.G,scene.Text.B):ActualTheme == ElementTheme.Dark ? Colors.White : Windows.UI.Color.FromArgb(255, 37, 37, 42);
        for (int i = 0; i < layouts.Count; i++)
        {
            var row = layouts[i]; float y = row.Y - offset;
            if (y + row.Height < -24 || y > canvas.ActualHeight + 24) continue;
            int distance = Math.Abs(i - active);
            bool clear = !lyrics.Timed || browsing || i == active || i == hover || active < 0;
            float sigma = clear || !BlurEnabled ? 0 : Math.Min(3.2f, distance * 1.2f);
            byte alpha = (byte)(clear ? 255 : i == active-1 ? 179 : i == active+1 ? 115 : distance == 2 ? 89 : 64);
            if (!clear && i == previousActive && !ReduceMotion) { float fade = Math.Clamp((float)(DateTime.UtcNow - focusChanged).TotalMilliseconds / 180, 0, 1); sigma *= fade; alpha = (byte)(255 + (alpha - 255) * fade); }
            var ink = Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);
            if (sigma == 0) args.DrawingSession.DrawTextLayout(row.Layout, 24, y, ink);
            else
            {
                var key=(i,(int)Math.Round(sigma*5),alpha);
                if(!effectCache.TryGetValue(key,out var effect)){
                    if(effectCache.Count>=48){var oldest=effectCache.First();oldest.Value.Blur.Dispose();oldest.Value.Image.Dispose();effectCache.Remove(oldest.Key);}
                    var command=new CanvasCommandList(sender);
                    using(var drawing=command.CreateDrawingSession())drawing.DrawTextLayout(row.Layout,24,0,ink);
                    effect=(command,new GaussianBlurEffect{Source=command,BlurAmount=key.Item2/5f});effectCache[key]=effect;
                }
                args.DrawingSession.DrawImage(effect.Blur,new Vector2(0,y));
            }
            var highlight=colorScene is {} currentScene?Windows.UI.Color.FromArgb(255,currentScene.Accent.R,currentScene.Accent.G,currentScene.Accent.B):Windows.UI.Color.FromArgb(255,217,71,104);
            if(i==active&&lyrics.Lines[i].Words is {} words)
                for(int wi=0;wi<words.Count;wi++){var word=words[wi];if(word.Time>lyricTime||word.Start<0||word.Start+word.Length>lyrics.Lines[i].Text.Length)continue;double end=wi+1<words.Count?words[wi+1].Time:i+1<lyrics.Lines.Count?lyrics.Lines[i+1].Time:word.Time+.6;float progress=ReduceMotion?1:(float)Math.Clamp((lyricTime-word.Time)/Math.Max(.05,end-word.Time),0,1);foreach(var region in row.Layout.GetCharacterRegions(word.Start,word.Length)){var clip=region.LayoutBounds;clip.X+=24;clip.Y+=y;clip.Width*=progress;using(var layer=args.DrawingSession.CreateLayer(1,clip))args.DrawingSession.DrawTextLayout(row.Layout,24,y,highlight);}}
            if (browsing && i == active) args.DrawingSession.FillCircle(new Vector2(8, y + 18), 3, highlight);
        }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new LyricsPeer(this);
    private sealed class LyricsPeer(LyricsView owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => "SynchronizedLyrics";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPatternCore(patternInterface);
        public bool IsReadOnly => true;
        public string Value => string.Join("\n", owner.lyrics.Lines.Select(line => owner.lyrics.Timed ? $"{TimeSpan.FromSeconds(line.Time):m\\:ss} {line.Text}" : line.Text));
        public void SetValue(string value) => throw new InvalidOperationException("歌词为只读内容");
    }
}
