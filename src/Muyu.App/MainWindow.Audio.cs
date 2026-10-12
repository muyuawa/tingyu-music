using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Muyu.Core;
using System.Security.Cryptography;
using System.Text;
namespace Muyu.App;
public sealed partial class MainWindow
{
    private AudioSettings savedAudio=new();
    private AudioSettings? previousAudio;
    private List<AudioAuthorization> audioAuthorizations=[];
    private List<AudioPolicyGrant> audioPolicyGrants=[],sessionAudioGrants=[];
    private async Task<bool> OfferCompatibleAudio(Track track)
    {
        if(savedAudio.Backend!=AudioBackend.WasapiExclusive||savedAudio.Dsd==DsdMode.DoP||audio.Status.Error!=unchecked((int)0x88890008))return false;
        if(!await Command(new("capability",Device:device,Settings:savedAudio)))return false;
        var formats=(audio.Capability?.Formats??[]).Where(f=>f.Channels<=track.Channels&&f.Channels<=2&&f.Rate<=384000).OrderBy(f=>f.Channels==track.Channels?0:1).ThenBy(f=>f.Rate==track.SampleRate?0:1).ThenBy(f=>f.Bits>=track.Bits?0:1).ThenBy(f=>Math.Abs(Math.Log((double)f.Rate/track.SampleRate))).ThenByDescending(f=>f.Bits).Distinct().Take(12).ToArray();
        var choices=new ComboBox{Header="已探测候选",ItemsSource=formats,SelectedIndex=formats.Length>0?0:-1,HorizontalAlignment=HorizontalAlignment.Stretch};var changes=new TextBlock{TextWrapping=TextWrapping.Wrap};void Describe(){if(choices.SelectedItem is AudioFormat f)changes.Text=$"目标 {f}\n"+(f.Rate!=track.SampleRate?"• 将重采样\n":"")+(f.Bits<track.Bits?"• 将降低位深，并应用 TPDF 抖动\n":f.Bits>track.Bits?"• 将扩展有效位深\n":"")+(f.Channels!=track.Channels?"• 将改变声道并下混\n":"")+"仅在确认后重新建立输出。";}choices.SelectionChanged+=(_,_)=>Describe();Describe();var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text=$"所选设备不接受当前格式。\n来源 {track.FormatText} / {track.Channels} 声道\n设备 {device}",TextWrapping=TextWrapping.Wrap});panel.Children.Add(choices);panel.Children.Add(changes);
        var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,RequestedTheme=EffectiveTheme,Title="选择本次兼容方式",Content=panel,PrimaryButtonText=formats.Length>0?"按所选格式独占播放":"",SecondaryButtonText="使用系统共享",CloseButtonText="取消"};var result=await dialog.ShowAsync();if(result==ContentDialogResult.None)return false;
        previousAudio=savedAudio;savedAudio=result==ContentDialogResult.Secondary?savedAudio with{Revision=savedAudio.Revision+1,Backend=AudioBackend.WasapiShared}:choices.SelectedItem is AudioFormat target?savedAudio with{Revision=savedAudio.Revision+1,TargetRate=target.Rate,TargetBits=target.Bits,TargetChannels=target.Channels}:throw new InvalidOperationException("请选择候选格式");shared=savedAudio.Backend==AudioBackend.WasapiShared;SynchronizeVolumeBackend(previousAudio.Backend);RefreshVolumeControls();Save("audio-settings",savedAudio);return await AuthorizeAudio(track);
    }
    private void InitializeAudioSettings(){savedAudio=Read<AudioSettings>("audio-settings")??new(Device:device,Backend:shared?AudioBackend.WasapiShared:AudioBackend.WasapiExclusive);try{savedAudio.Validate();}catch{savedAudio=new(Device:device);}audioAuthorizations=Read<List<AudioAuthorization>>("audio-authorizations")??[];audioPolicyGrants=Read<List<AudioPolicyGrant>>("audio-policy-grants")??[];device=savedAudio.Device;shared=savedAudio.Backend==AudioBackend.WasapiShared;}
    private async Task<bool> AuthorizeAudio(Track track)
    {
        savedAudio=savedAudio with{Device=device};
        bool dsd=new[]{".dsf",".dff"}.Contains(Path.GetExtension(track.Path),StringComparer.OrdinalIgnoreCase);
        if(volumeMode=="software" && !(savedAudio with{GainDb=0}).Processing && savedAudio.Backend!=AudioBackend.WasapiShared && !dsd && track.Encoding!=AudioEncoding.FloatPcm)return true;
        if(!savedAudio.Processing&&savedAudio.Backend!=AudioBackend.WasapiShared&&!dsd&&track.Encoding!=AudioEncoding.FloatPcm)return true;
        if(!await Command(new("capability",Device:device,Settings:savedAudio)))return false;
        string fingerprint=audio.Capability?.Fingerprint??throw new IOException("无法读取设备能力指纹");
        if(audioPolicyGrants.Concat(sessionAudioGrants).Any(a=>a.Matches(savedAudio,track,fingerprint)))return true;
        if(audioAuthorizations.Any(a=>a.Matches(savedAudio,track,fingerprint))){var migrated=AudioPolicyGrant.Accept(savedAudio,track,fingerprint);audioPolicyGrants.Add(migrated);Save("audio-policy-grants",audioPolicyGrants);return true;}
        var remember=new CheckBox{Content="记住此设备和输出策略，下次启动继续使用",IsChecked=true};
        var panel=new StackPanel{Spacing=12};panel.Children.Add(new TextBlock{Text=$"来源：{track.SampleRate} Hz / {track.Bits} bit / {track.Channels} 声道\n目标：{savedAudio.TargetRate??track.SampleRate} Hz / {savedAudio.TargetBits??track.Bits} bit / {savedAudio.TargetChannels??track.Channels} 声道\n软件增益 {savedAudio.GainDb:0.#} dB · ReplayGain {savedAudio.ReplayGain}\n{(savedAudio.Backend==AudioBackend.WasapiShared?"Windows 将接管共享混音及可能的采样转换。":"应用将按以上设置修改 PCM 样本。")}",TextWrapping=TextWrapping.Wrap});panel.Children.Add(remember);
        if(track.Encoding==AudioEncoding.FloatPcm)panel.Children.Add(new TextBlock{Text="当前浮点音源将转为 32 位整数 PCM；超出满幅的数值会截断。此转换不作原始浮点样本直通承诺。",TextWrapping=TextWrapping.Wrap});
        if(dsd)panel.Children.Add(new TextBlock{Text=savedAudio.Dsd==DsdMode.DoP?"DoP 传送原始 DSD 位，仅用于明确支持 DoP 的接收设备。输出为 24 位封装、DSD 码率的 1/16；无软件音量和均衡处理。":"当前选择将 DSD 解码为 PCM，并以该 PCM 格式建立输出。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="确认后，同类音源、相同声道的后续歌曲沿用此输出策略；软件音量微调不再重复询问。更换设备、转换方式或输入类型时重新确认。取消勾选只记住到本次退出。",TextWrapping=TextWrapping.Wrap,FontSize=12});
        if(await Dialog("确认音频输出策略",panel,"确认并继续")!=ContentDialogResult.Primary)return false;
        var grant=AudioPolicyGrant.Accept(savedAudio,track,fingerprint);sessionAudioGrants.Add(grant);
        if(remember.IsChecked==true){audioPolicyGrants.Add(grant);Save("audio-policy-grants",audioPolicyGrants);}return true;
    }
    private async Task ApplyAudio(AudioSettings next,bool now,bool resumeRequest=false)
    {
        next.Validate();previousAudio=savedAudio;next=next with{Revision=savedAudio.Revision+1};Save("audio-settings",next);savedAudio=next;device=next.Device;shared=next.Backend==AudioBackend.WasapiShared;
        SynchronizeVolumeBackend(previousAudio.Backend);
        RefreshVolumeControls();
        if(!now||current==null){Show("设置已保存，将在下次建立输出时生效");return;}
        bool wasPlaying=audio.Status.State==2||resumeRequest;var track=current;
        if(!waitingToResume){if(!await Command(new("pause")))return;SynchronizeCurrent();CaptureReliablePosition();track=current!;}
        long frame=rememberedFrame;SaveQueue(false);if(!await Command(new("stop")))return;waitingToResume=true;UpdateStoredPosition();
        if(!wasPlaying&&savedAudio.Backend==AudioBackend.WasapiExclusive){exclusiveResumeGrant=null;clock.Stop();return;}
        clock.Start();await Start(track,position:(double)frame/track.SampleRate,autoPlay:wasPlaying);
    }
    private async void Devices_Click(object sender,RoutedEventArgs e)
    {
        try
        {
            var devices=await Task.Run(NativeAudio.Devices);await Command(new("asio-drivers"));
            var backend=new ComboBox{Header="输出接口",ItemsSource=new[]{"WASAPI 独占","WASAPI 共享","ASIO"},SelectedIndex=(int)savedAudio.Backend,HorizontalAlignment=HorizontalAlignment.Stretch};
            var choices=new ComboBox{Header="输出设备",HorizontalAlignment=HorizontalAlignment.Stretch,MinWidth=320};
            var follow=new CheckBox{Content="跟随系统默认设备（下次建立输出时生效）",IsChecked=savedAudio.FollowSystemDefault};
            void UpdateDevices(){if(backend.SelectedIndex==2){choices.ItemsSource=audio.AsioDrivers;choices.SelectedItem=audio.AsioDrivers.FirstOrDefault(d=>d==savedAudio.Device);}else{choices.ItemsSource=devices;choices.SelectedItem=devices.FirstOrDefault(d=>d.Id==savedAudio.Device);}choices.PlaceholderText="请选择设备（原设备不可用时不自动切换）";}
            backend.SelectionChanged+=(_,_)=>UpdateDevices();UpdateDevices();
            var buffer=new NumberBox{Header="请求缓冲（ms；ASIO 采用驱动缓冲）",Value=savedAudio.BufferMilliseconds,Minimum=10,Maximum=1000};
            var rate=new ComboBox{Header="采样率",ItemsSource=new[]{"保留来源","44100","48000","88200","96000","176400","192000","352800","384000"},SelectedIndex=0};if(savedAudio.TargetRate!=null)rate.SelectedItem=savedAudio.TargetRate.Value.ToString();
            var bits=new ComboBox{Header="有效位深",ItemsSource=new[]{"保留来源","16","24","32"},SelectedIndex=0};if(savedAudio.TargetBits!=null)bits.SelectedItem=savedAudio.TargetBits.Value.ToString();
            var channels=new ComboBox{Header="声道",ItemsSource=new[]{"保留来源","1","2"},SelectedIndex=savedAudio.TargetChannels??0};
            var gain=new NumberBox{Header="软件增益（dB；0 保持原样）",Value=savedAudio.GainDb,Minimum=-40,Maximum=12,SmallChange=.1,LargeChange=2,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Inline};
            var rg=new ComboBox{Header="ReplayGain",ItemsSource=new[]{"关闭","音轨","专辑"},SelectedIndex=(int)savedAudio.ReplayGain};
            var clip=new CheckBox{Content="依据 ReplayGain 峰值防止削波",IsChecked=savedAudio.PreventClipping};
            var dsdMode=new ComboBox{Header="DSD 输出",ItemsSource=new[]{"明确解码为 PCM","DoP 1.1（接收设备须支持）"},SelectedIndex=(int)savedAudio.Dsd};
            var eq=new List<NumberBox>();var panel=new StackPanel{Spacing=12};
            foreach(var control in new UIElement[]{backend,choices,follow,buffer,rate,bits,channels,gain,rg,clip,dsdMode})panel.Children.Add(control);
            string[] labels=["31 Hz","62 Hz","125 Hz","250 Hz","500 Hz","1 kHz","2 kHz","4 kHz","8 kHz","16 kHz"];
            for(int i=0;i<10;i++){var box=new NumberBox{Header="均衡器 · "+labels[i]+"（dB）",Minimum=-12,Maximum=12,Value=savedAudio.Equalizer?[i]??0};eq.Add(box);panel.Children.Add(box);}
            panel.Children.Add(new TextBlock{Text="0 dB、保留来源、关闭 ReplayGain 为原始模式。修改设置须在播放前确认处理。",TextWrapping=TextWrapping.Wrap});
            var revoke=new Button{Content=$"撤销处理授权（{audioAuthorizations.Count+audioPolicyGrants.Count}）"};revoke.Click+=(_,_)=>{audioAuthorizations.Clear();audioPolicyGrants.Clear();sessionAudioGrants.Clear();Save("audio-authorizations",audioAuthorizations);Save("audio-policy-grants",audioPolicyGrants);revoke.Content="已撤销；当前音频流继续";};panel.Children.Add(revoke);
            var authorizations=new StackPanel{Spacing=8};foreach(var authorization in audioAuthorizations.ToArray()){var row=new StackPanel{Spacing=5};row.Children.Add(new TextBlock{Text=$"{authorization.Created.LocalDateTime:g}\n{authorization.Device}\n{authorization.SourceKind} · {authorization.SourceRate} Hz / {authorization.SourceBits} bit / {authorization.SourceChannels} 声道\n策略 {authorization.StrategyHash[..Math.Min(12,authorization.StrategyHash.Length)]} · 设备指纹 {authorization.CapabilityHash[..Math.Min(12,authorization.CapabilityHash.Length)]}",TextWrapping=TextWrapping.Wrap});var remove=new Button{Content="撤销这条授权"};remove.Click+=(_,_)=>{audioAuthorizations.RemoveAll(a=>a.Id==authorization.Id);Save("audio-authorizations",audioAuthorizations);authorizations.Children.Remove(row);};row.Children.Add(remove);authorizations.Children.Add(row);}foreach(var policy in audioPolicyGrants.ToArray()){var row=new StackPanel{Spacing=5};row.Children.Add(new TextBlock{Text=$"{policy.Created.LocalDateTime:g} · {policy.SourceKind} / {policy.SourceChannels} 声道\n设备 {policy.Device}\n固定输出策略 · 包含后续同类歌曲与音量微调",TextWrapping=TextWrapping.Wrap});var remove=new Button{Content="撤销这条策略授权"};remove.Click+=(_,_)=>{audioPolicyGrants.RemoveAll(a=>a.Id==policy.Id);sessionAudioGrants.RemoveAll(a=>a.PolicyHash==policy.PolicyHash&&a.Device==policy.Device&&a.SourceKind==policy.SourceKind&&a.SourceChannels==policy.SourceChannels);Save("audio-policy-grants",audioPolicyGrants);authorizations.Children.Remove(row);};row.Children.Add(remove);authorizations.Children.Add(row);}panel.Children.Add(new Expander{Header="查看与逐条撤销兼容授权",Content=authorizations,HorizontalAlignment=HorizontalAlignment.Stretch});
            var restore=new CheckBox{Content="恢复上次设置并继续",IsEnabled=previousAudio!=null};panel.Children.Add(restore);
            var dialog=new ContentDialog{XamlRoot=Root.XamlRoot,RequestedTheme=EffectiveTheme,Title="音频设置 · 编辑草稿",Content=new ScrollViewer{Content=panel,MaxHeight=510},PrimaryButtonText="立即应用",SecondaryButtonText="保存，稍后生效",CloseButtonText="取消"};
            var validation=new TextBlock{TextWrapping=TextWrapping.Wrap};panel.Children.Add(validation);AudioSettings? draft=null;
            AudioSettings BuildDraft(){if(restore.IsChecked==true&&previousAudio!=null)return previousAudio;if(choices.SelectedItem is AudioDevice unavailable&&!unavailable.Available&&follow.IsChecked!=true)throw new InvalidOperationException("所选设备不可用，请选择在线设备");string id=follow.IsChecked==true?"":choices.SelectedItem is AudioDevice d?d.Id:choices.SelectedItem as string??throw new InvalidOperationException("请选择输出设备");int? Parse(ComboBox box)=>box.SelectedIndex<=0?null:int.Parse((string)box.SelectedItem);var value=new AudioSettings(Backend:(AudioBackend)backend.SelectedIndex,Device:id,BufferMilliseconds:(int)buffer.Value,TargetRate:Parse(rate),TargetBits:Parse(bits),TargetChannels:Parse(channels),GainDb:gain.Value,ReplayGain:(ReplayGainMode)rg.SelectedIndex,PreventClipping:clip.IsChecked==true,Equalizer:eq.Select(x=>x.Value).ToArray(),Dsd:(DsdMode)dsdMode.SelectedIndex,FollowSystemDefault:follow.IsChecked==true);value.Validate();return value;}
            void ValidateDraft(ContentDialogButtonClickEventArgs args){try{draft=BuildDraft();}catch(Exception ex){args.Cancel=true;validation.Text=ex.Message;}}
            dialog.PrimaryButtonClick+=(_,args)=>ValidateDraft(args);dialog.SecondaryButtonClick+=(_,args)=>ValidateDraft(args);
            var result=await dialog.ShowAsync();if(result==ContentDialogResult.None)return;
            if(restore.IsChecked==true&&previousAudio!=null){await ApplyAudio(previousAudio,true,true);return;}
            await ApplyAudio(draft??throw new InvalidOperationException("音频草稿尚未验证"),result==ContentDialogResult.Primary);
        }
        catch(Exception ex){Show("音频设置未应用："+ex.Message,true);}
    }
    private async void AudioInfo_Click(object sender,RoutedEventArgs e)
    {
        var actual=audio.Actual;
        string Backend(AudioBackend backend)=>backend switch{AudioBackend.WasapiShared=>"WASAPI 共享",AudioBackend.WasapiExclusive=>"WASAPI 独占",_=>"ASIO"};
        string text=$"输出模式：{Backend(savedAudio.Backend)}\n"+(actual==null?"当前未建立音频输出":$"来源：{actual.SourceRate} Hz / {actual.SourceBits} bit / {actual.SourceChannels} 声道\n输出：{actual.OutputRate} Hz / {actual.OutputBits} bit / {actual.OutputChannels} 声道\n缓冲：{1000.0*actual.ActualBufferFrames/actual.OutputRate:0.0} ms");
        if(!string.IsNullOrEmpty(audio.Error))text+=$"\n\n最近播放错误：{audio.Error}";
        var panel=new StackPanel{Spacing=14};panel.Children.Add(new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
#if MUYU_DIAGNOSTICS
        var copy=new Button{Content="复制播放诊断"};copy.Click+=(_,_)=>{var package=new Windows.ApplicationModel.DataTransfer.DataPackage();package.SetText(PlaybackDiagnostic());Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);copy.Content="已复制";};panel.Children.Add(copy);
#endif
        await Dialog("音频信息",new ScrollViewer{MaxHeight=520,Content=panel});
    }

    private string? lastAudioFailure;
#if MUYU_DIAGNOSTICS
    private string PlaybackDiagnostic()=>System.Text.Json.JsonSerializer.Serialize(new{version=typeof(MainWindow).Assembly.GetName().Version?.ToString(),time=DateTimeOffset.Now,song=current?.Title,source_rate=current?.SampleRate,source_bits=current?.Bits,source_channels=current?.Channels,encoding=current?.Encoding.ToString(),settings=savedAudio,actual=audio.Actual,status=audio.Status,error=audio.Error,last_failure=lastAudioFailure},new System.Text.Json.JsonSerializerOptions{WriteIndented=true,IncludeFields=true});
#endif
    private void RecordAudioFailure(Exception exception,string action,Track? track)
    {
        lastAudioFailure=System.Text.Json.JsonSerializer.Serialize(new{time=DateTimeOffset.Now,action,song=track?.Title,source_rate=track?.SampleRate,source_bits=track?.Bits,source_channels=track?.Channels,encoding=track?.Encoding.ToString(),settings=savedAudio,error_code=$"0x{audio.Status.Error:X8}",exception_code=$"0x{exception.HResult:X8}",message=exception.Message});
        try{File.AppendAllText(Path.Combine(dataFolder,"audio-errors.log"),lastAudioFailure+Environment.NewLine);}catch(IOException){}catch(UnauthorizedAccessException){}
    }
}
