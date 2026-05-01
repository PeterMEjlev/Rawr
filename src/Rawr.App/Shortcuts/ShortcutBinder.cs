using System.Windows.Input;

namespace Rawr.App.Shortcuts;

public static class ShortcutBinder
{
    /// <summary>
    /// Resolves the effective binding for an action: a stored non-empty override if present,
    /// an explicit "unbound" if the override is empty, otherwise the action's default.
    /// </summary>
    public static (KeySpec? Spec, bool Unbound) ResolveBinding(AppSettings settings, ShortcutAction action)
    {
        if (settings.KeyBindings.TryGetValue(action.Id, out var raw))
        {
            if (string.IsNullOrWhiteSpace(raw))
                return (null, true);

            var parsed = KeySpec.TryParse(raw);
            if (parsed is not null)
                return (parsed, false);
            // Fall through to default if parsing failed.
        }
        return (action.DefaultBinding, false);
    }

    public static void ApplyTo(MainWindow window, AppSettings settings)
    {
        // We own the entire InputBindings collection on MainWindow — clear and rebuild.
        window.InputBindings.Clear();

        foreach (var action in ShortcutRegistry.All)
        {
            var (spec, _) = ResolveBinding(settings, action);
            if (spec is null) continue;

            var cmd = action.ResolveCommand(window);
            if (cmd is null) continue;

            // Construct via property setters rather than the (cmd, Key, Modifiers) ctor —
            // the ctor routes through KeyGesture validation which rejects unmodified
            // letter keys like 'P' or 'T'. Property setters skip that check, matching
            // the XAML behaviour.
            var kb = new KeyBinding { Command = cmd, Key = spec.Key, Modifiers = spec.Modifiers };
            if (action.CommandParameter is not null)
                kb.CommandParameter = action.CommandParameter;
            window.InputBindings.Add(kb);
        }
    }
}
