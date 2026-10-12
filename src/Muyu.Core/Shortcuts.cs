namespace Muyu.Core;
public enum PlayerAction { PlayPause,OpenFiles,OpenFolder,Search,UndoQueue,Previous,Next,Stop,Mini,DesktopLyrics }
public sealed record ShortcutBinding(PlayerAction Action,int Key,bool Control=false,bool Shift=false,bool Alt=false);
public static class Shortcuts
{
    public static ShortcutBinding? Resolve(IEnumerable<ShortcutBinding> bindings,int key,bool control,bool shift,bool alt,bool textInput=false,bool modal=false,bool localControl=false)
    {
        if(modal)return null;
        var binding=bindings.FirstOrDefault(b=>b.Key==key&&b.Control==control&&b.Shift==shift&&b.Alt==alt);
        if(binding==null||textInput&&binding.Action!=PlayerAction.Search||localControl&&!control&&!alt)return null;
        return binding;
    }
    public static ShortcutBinding[] Defaults=>[new(PlayerAction.PlayPause,32),new(PlayerAction.OpenFiles,79,true),new(PlayerAction.OpenFolder,79,true,true),new(PlayerAction.Search,70,true),new(PlayerAction.UndoQueue,90,true),new(PlayerAction.Previous,37,true),new(PlayerAction.Next,39,true),new(PlayerAction.Stop,83,true),new(PlayerAction.Mini,77,true),new(PlayerAction.DesktopLyrics,76,true,true)];
    public static void Validate(IReadOnlyList<ShortcutBinding> bindings){if(bindings.Count!=Enum.GetValues<PlayerAction>().Length||bindings.Any(b=>b==null||!Enum.IsDefined(b.Action)||b.Key is <8 or >254||b.Alt&&b.Key==115||b.Control&&b.Alt&&b.Key==46)||bindings.Select(b=>b.Action).Distinct().Count()!=bindings.Count)throw new FormatException("快捷键定义不完整，或使用了系统保留按键");if(bindings.Select(b=>(b.Key,b.Control,b.Shift,b.Alt)).Distinct().Count()!=bindings.Count)throw new FormatException("快捷键有冲突，请为每个操作选择不同组合");}
}
