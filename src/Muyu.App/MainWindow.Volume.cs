using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using Muyu.Core;

namespace Muyu.App;
public sealed partial class MainWindow
{
    private string volumeMode="original";
    private bool settingVolume,draggingVolume,applyingVolume,softwareMuted,sessionMuted,deviceMuted;
    private double requestedVolume,sessionVolume=1;
    private int? appliedSessionProcess;
    private Slider? popupVolumeSlider;
    private TextBlock? popupVolumeValue;
    private NumberBox? popupVolumeInput;
    private ToggleSwitch? popupVolumeMute;
    private readonly DispatcherTimer volumeTimer=new(){Interval=TimeSpan.FromMilliseconds(100)};
    private readonly DispatcherTimer volumePoll=new(){Interval=TimeSpan.FromSeconds(1)};

    private void InitializeVolumeControls()
    {
        volumeMode=store.Get("volume-mode")??(savedAudio.Backend==AudioBackend.WasapiShared?"session":savedAudio.GainDb!=0?"software":"original");
        sessionVolume=Read<double?>("session-volume")??1;
        softwareMuted=store.Get("software-muted")=="true";sessionMuted=store.Get("session-muted")=="true";
        VolumeSlider.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>draggingVolume=true),true);
        VolumeSlider.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>{draggingVolume=false;volumeTimer.Start();}),true);
        VolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler((_,_)=>{draggingVolume=false;volumeTimer.Start();}),true);
        volumeTimer.Tick+=async(_,_)=>{if(volumeMode=="software"&&draggingVolume)return;volumeTimer.Stop();await CommitVolume();};
        volumePoll.Tick+=(_,_)=>PollVolume();
        volumePoll.Start();RefreshVolumeControls();
    }

    private void SynchronizeVolumeBackend(AudioBackend previous)
    {
        if(previous==savedAudio.Backend)return;
        appliedSessionProcess=null;
        if(savedAudio.Backend==AudioBackend.WasapiShared&&volumeMode=="original")volumeMode="session";
        else if(savedAudio.Backend!=AudioBackend.WasapiShared&&volumeMode=="session")volumeMode="original";
        store.Set("volume-mode",volumeMode);
    }

    private MMDevice VolumeEndpoint(MMDeviceEnumerator enumerator)
    {
        string id=audio.Actual?.Device??device;
        return string.IsNullOrEmpty(id)?enumerator.GetDefaultAudioEndpoint(DataFlow.Render,Role.Multimedia):enumerator.GetDevice(id);
    }
    private bool HasHardwareVolume()
    {
        if(savedAudio.Backend==AudioBackend.Asio)return false;
        try{using var enumerator=new MMDeviceEnumerator();using var endpoint=VolumeEndpoint(enumerator);return ((int)endpoint.AudioEndpointVolume.HardwareSupport&1)!=0;}catch{return false;}
    }
    private float? ReadEndpointVolume()
    {
        using var enumerator=new MMDeviceEnumerator();using var endpoint=VolumeEndpoint(enumerator);
        deviceMuted=endpoint.AudioEndpointVolume.Mute;
        return deviceMuted?0:endpoint.AudioEndpointVolume.MasterVolumeLevelScalar;
    }
    private float? SessionLevel(float? value=null)
    {
        if(audio.ProcessId is not {} process)return null;
        using var enumerator=new MMDeviceEnumerator();using var endpoint=VolumeEndpoint(enumerator);
        var sessions=endpoint.AudioSessionManager.Sessions;
        for(int i=0;i<sessions.Count;i++)
        {
            using var session=sessions[i];
            if(session.GetProcessID!=process)continue;
            if(value.HasValue){session.SimpleAudioVolume.Volume=value.Value;session.SimpleAudioVolume.Mute=value.Value<=0;}
            sessionMuted=session.SimpleAudioVolume.Mute;
            return sessionMuted?0:session.SimpleAudioVolume.Volume;
        }
        return null;
    }
    private void PollVolume()
    {
        if(!MainSurfaceVisible||closed||settingVolume||draggingVolume||applyingVolume||volumeTimer.IsEnabled)return;
        try
        {
            if(volumeMode=="device")RefreshVolumeControls(ReadEndpointVolume()*100);
            if(volumeMode=="session"&&savedAudio.Backend==AudioBackend.WasapiShared)
            {
                if(audio.ProcessId!=appliedSessionProcess&&SessionLevel((float)(sessionMuted?0:sessionVolume)).HasValue)appliedSessionProcess=audio.ProcessId;
                RefreshVolumeControls(SessionLevel()*100??sessionVolume*100);
            }
        }catch(Exception ex){VolumeSlider.IsEnabled=false;ToolTipService.SetToolTip(VolumeSlider,"音量控制暂不可用："+ex.Message);}
    }
    private void RefreshVolumeControls(double? actual=null)
    {
        settingVolume=true;
        bool software=volumeMode=="software",dop=savedAudio.Dsd==DsdMode.DoP;
        double value=actual??(software?savedAudio.GainDb:volumeMode=="session"?sessionVolume*100:100);
        foreach(var slider in new[]{VolumeSlider,popupVolumeSlider}.OfType<Slider>())
        {
            slider.Minimum=software?-40:0;slider.Maximum=software?12:100;slider.StepFrequency=.1;
            slider.SmallChange=.1;slider.LargeChange=software?2:5;
            slider.Value=Math.Clamp(value,slider.Minimum,slider.Maximum);
            slider.IsEnabled=volumeMode=="device"||volumeMode=="session"&&savedAudio.Backend==AudioBackend.WasapiShared||software&&!dop;
            ToolTipService.SetToolTip(slider,volumeMode=="original"?"原始输出 · 请使用设备音量，或在音量浮层启用软件音量":software?$"软件音量 {value:0.##} dB · PCM 已处理":$"{value:0.#}%");
        }
        if(popupVolumeValue!=null)popupVolumeValue.Text=volumeMode=="original"?"原始输出":software?$"{value:0.0} dB":$"{value:0.#}%";
        bool muted=software?softwareMuted:volumeMode=="session"?sessionMuted:volumeMode=="device"&&deviceMuted;
        VolumeIcon.Glyph=muted?"\uE74F":"\uE767";
        if(popupVolumeInput!=null){popupVolumeInput.Header=software?"精细调整（dB）":"精细调整（%）";popupVolumeInput.Minimum=VolumeSlider.Minimum;popupVolumeInput.Maximum=VolumeSlider.Maximum;popupVolumeInput.SmallChange=.1;popupVolumeInput.LargeChange=software?2:5;popupVolumeInput.Value=VolumeSlider.Value;popupVolumeInput.IsEnabled=VolumeSlider.IsEnabled;}
        if(popupVolumeMute!=null){popupVolumeMute.Header=volumeMode=="device"?"设备静音（影响整个设备）":"静音";popupVolumeMute.IsOn=muted;popupVolumeMute.IsEnabled=volumeMode!="original"&&(!software||!dop);}
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(VolumeSlider,software?"软件音量（分贝）":volumeMode=="device"?"设备主音量（影响整个设备）":"当前播放器音量");
        settingVolume=false;
    }
    private void VolumeSlider_Changed(object sender,Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs args)
    {
        if(settingVolume||store==null)return;
        requestedVolume=args.NewValue;
        settingVolume=true;
        if(!ReferenceEquals(sender,VolumeSlider))VolumeSlider.Value=args.NewValue;
        if(popupVolumeSlider!=null&&!ReferenceEquals(sender,popupVolumeSlider))popupVolumeSlider.Value=args.NewValue;
        if(popupVolumeInput!=null)popupVolumeInput.Value=args.NewValue;
        if(popupVolumeValue!=null)popupVolumeValue.Text=volumeMode=="software"?$"{args.NewValue:0.0} dB":$"{args.NewValue:0.#}%";
        settingVolume=false;volumeTimer.Stop();volumeTimer.Start();
    }
    private async Task CommitVolume()
    {
        if(applyingVolume||closed)return;
        applyingVolume=true;
        try
        {
            if(volumeMode=="software")
            {
                if(savedAudio.Dsd==DsdMode.DoP)throw new InvalidOperationException("DoP 使用设备音量，不能应用软件音量");
                await ApplyAudio(savedAudio with{GainDb=requestedVolume},true);
            }
            else if(volumeMode=="device")
            {
                using var enumerator=new MMDeviceEnumerator();using var endpoint=VolumeEndpoint(enumerator);
                if(((int)endpoint.AudioEndpointVolume.HardwareSupport&1)==0)throw new InvalidOperationException("当前设备没有声明硬件音量控制");
                endpoint.AudioEndpointVolume.MasterVolumeLevelScalar=(float)(requestedVolume/100);
                if(requestedVolume>0)endpoint.AudioEndpointVolume.Mute=false;
            }
            else if(volumeMode=="session"&&savedAudio.Backend==AudioBackend.WasapiShared)
            {
                sessionVolume=requestedVolume/100;Save("session-volume",sessionVolume);SessionLevel((float)sessionVolume);
                store.Set("session-muted",sessionMuted?"true":"false");
            }
            RefreshVolumeControls(requestedVolume);
        }
        catch(Exception ex){Show("音量未应用："+ex.Message,true);RefreshVolumeControls();}
        finally{applyingVolume=false;}
    }
    private async Task CommitMute(bool muted)
    {
        if(applyingVolume||closed){RefreshVolumeControls();return;}
        applyingVolume=true;
        try
        {
            if(volumeMode=="software")
            {
                if(savedAudio.Dsd==DsdMode.DoP)throw new InvalidOperationException("DoP 请使用设备硬件静音");
                if(audio.Actual!=null&&audio.Status.State is 1 or 2&&!await Command(new("mute",Frame:muted?1:0)))throw new InvalidOperationException("未能设置软件静音");
                softwareMuted=muted;store.Set("software-muted",muted?"true":"false");
            }
            else if(volumeMode=="session"&&savedAudio.Backend==AudioBackend.WasapiShared)
            {
                sessionMuted=muted;SessionLevel((float)(muted?0:sessionVolume));store.Set("session-muted",muted?"true":"false");
            }
            else if(volumeMode=="device")
            {
                using var enumerator=new MMDeviceEnumerator();using var endpoint=VolumeEndpoint(enumerator);
                if(((int)endpoint.AudioEndpointVolume.HardwareSupport&2)==0)throw new InvalidOperationException("设备没有声明硬件静音，请使用设备旋钮或选择软件音量");
                endpoint.AudioEndpointVolume.Mute=muted;deviceMuted=muted;
            }
        }
        catch(Exception ex){Show("静音未应用："+ex.Message,true);}
        finally{applyingVolume=false;RefreshVolumeControls();PollVolume();}
    }
    private async void Volume_Click(object sender,RoutedEventArgs args)
    {
        var choices=new RadioButtons{Header="控制方式",MaxColumns=4,HorizontalAlignment=HorizontalAlignment.Stretch};
        var modes=new List<(string Id,string Text)>{("original","原始输出 · 使用设备旋钮")};
        if(savedAudio.Backend==AudioBackend.WasapiShared)modes.Add(("session","当前播放器会话音量"));
        if(savedAudio.Dsd!=DsdMode.DoP)modes.Add(("software","启用软件音量 · PCM 将被处理"));
        if(HasHardwareVolume())modes.Add(("device","控制设备音量 · 影响整个设备"));
        choices.ItemsSource=modes.Select(m=>m.Id switch{"original"=>"原始","software"=>"软件","session"=>"会话",_=>"设备"}).ToArray();choices.SelectedIndex=Math.Max(0,modes.FindIndex(m=>m.Id==volumeMode));
        popupVolumeValue=new TextBlock{FontSize=34,FontFamily=new FontFamily("Cascadia Mono, Consolas"),FontWeight=Microsoft.UI.Text.FontWeights.SemiBold};
        popupVolumeSlider=new Slider{MinWidth=240};RefreshVolumeControls();popupVolumeSlider.ValueChanged+=VolumeSlider_Changed;
        popupVolumeInput=new NumberBox{SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
        popupVolumeInput.ValueChanged+=(_,e)=>{if(!settingVolume&&double.IsFinite(e.NewValue))VolumeSlider.Value=e.NewValue;};
        popupVolumeMute=new ToggleSwitch{OnContent="已静音",OffContent="有声"};var ownedMute=popupVolumeMute;popupVolumeMute.Toggled+=async(_,_)=>{if(!settingVolume)await CommitMute(ownedMute.IsOn);};RefreshVolumeControls();
        popupVolumeSlider.AddHandler(UIElement.PointerPressedEvent,new PointerEventHandler((_,_)=>draggingVolume=true),true);
        popupVolumeSlider.AddHandler(UIElement.PointerReleasedEvent,new PointerEventHandler((_,_)=>{draggingVolume=false;volumeTimer.Start();}),true);
        popupVolumeSlider.AddHandler(UIElement.PointerCaptureLostEvent,new PointerEventHandler((_,_)=>{draggingVolume=false;volumeTimer.Start();}),true);
        var explanation=new TextBlock{TextWrapping=TextWrapping.Wrap,Text="软件音量 −40～+12 dB，步进 0.1 dB；可用数字框精调。正增益可能使满幅音源削波。软件静音提交全零 PCM，取消后恢复原音量，播放进度继续。原始输出锁定数字音量；DoP 请使用设备硬件控制。"};
        choices.SelectionChanged+=async(_,_)=>
        {
            string next=modes[choices.SelectedIndex].Id;
            if(next==volumeMode)return;
            volumeTimer.Stop();draggingVolume=false;
            string previous=volumeMode;
            try
            {
                volumeMode=next;
                if(next!="software"&&savedAudio.GainDb!=0)await ApplyAudio(savedAudio with{GainDb=0},true);
                if(audio.Actual!=null&&audio.Status.State is 1 or 2){if(next!="software"&&audio.Actual.Muted)await Command(new("mute",Frame:0));else if(next=="software"&&softwareMuted)await Command(new("mute",Frame:1));}
                store.Set("volume-mode",next);
                RefreshVolumeControls();PollVolume();
            }
            catch(Exception ex){volumeMode=previous;Show("音量方式未切换："+ex.Message,true);choices.SelectedIndex=modes.FindIndex(m=>m.Id==volumeMode);}
        };
        var help=new Button{Content="ⓘ",Width=32,Height=32,MinHeight=32,Padding=new(0),Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent),BorderThickness=new(0)};
        ToolTipService.SetToolTip(help,explanation.Text);Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(help,"音量控制说明");
        var headline=new Grid();headline.ColumnDefinitions.Add(new(){Width=new(1,GridUnitType.Star)});headline.ColumnDefinitions.Add(new(){Width=GridLength.Auto});headline.Children.Add(popupVolumeValue);Grid.SetColumn(help,1);headline.Children.Add(help);
        var advanced=new StackPanel{Spacing=12};advanced.Children.Add(popupVolumeInput);advanced.Children.Add(choices);
        var panel=new StackPanel{Spacing=16};panel.Children.Add(headline);panel.Children.Add(popupVolumeSlider);panel.Children.Add(popupVolumeMute);panel.Children.Add(new Expander{Header="精细调整与控制方式",Content=advanced,HorizontalAlignment=HorizontalAlignment.Stretch});
        var ownedSlider=popupVolumeSlider;
        try{await ShowGlassPopover("音量",panel,380);}finally{if(ReferenceEquals(popupVolumeSlider,ownedSlider)){popupVolumeSlider=null;popupVolumeValue=null;popupVolumeInput=null;popupVolumeMute=null;draggingVolume=false;}}
    }
}
