using Muyu.Core;

static class ShortcutRoutingTests
{
    public static void Run(Action<bool,string> check)
    {
        var defaults=Shortcuts.Defaults;
        foreach(var binding in defaults)
        {
            check(Shortcuts.Resolve(defaults,binding.Key,binding.Control,binding.Shift,binding.Alt)?.Action==binding.Action,$"Shortcut {binding.Action} works with player button focus");
            check(Shortcuts.Resolve(defaults,binding.Key,binding.Control,binding.Shift,binding.Alt,modal:true)==null,$"Shortcut {binding.Action} does not escape a modal dialog");
            check((Shortcuts.Resolve(defaults,binding.Key,binding.Control,binding.Shift,binding.Alt,textInput:true)!=null)==(binding.Action==PlayerAction.Search),$"Shortcut {binding.Action} respects text editing");
        }
        check(Shortcuts.Resolve(defaults,32,false,false,false)?.Action==PlayerAction.PlayPause,"Space after previous/next remains PlayPause");
        check(Shortcuts.Resolve(defaults,32,true,false,false)==null,"Ctrl+Space does not accidentally invoke plain Space");
        check(Shortcuts.Resolve(defaults,37,false,false,false,localControl:true)==null,"Unmodified arrows stay with slider or selector");
        check(Shortcuts.Resolve(defaults,37,true,false,false,localControl:true)?.Action==PlayerAction.Previous,"Ctrl+Left remains available outside text editors");
        check(Shortcuts.Resolve(defaults,32,false,false,false,localControl:true)==null,"Space on a selector retains its native selection action");
        var custom=defaults.Select(b=>b.Action==PlayerAction.PlayPause?b with{Key=80,Control=true}:b).ToArray();
        Shortcuts.Validate(custom);
        check(Shortcuts.Resolve(custom,80,true,false,false)?.Action==PlayerAction.PlayPause,"Custom binding replaces default play key");
        check(Shortcuts.Resolve(custom,32,false,false,false)==null,"Old play key stops matching after customization");
    }
}
