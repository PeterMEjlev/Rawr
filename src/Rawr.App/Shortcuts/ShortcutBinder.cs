using System.Windows.Controls;
using System.Windows.Input;

namespace Rawr.App.Shortcuts;

public static class ShortcutBinder
{
    // Wraps a command so it refuses to execute when a text-input control has keyboard
    // focus. Applied to modifier-free shortcuts (plain letter keys) so typing in any
    // TextBox doesn't accidentally fire shortcuts like T (tags) or G (group toggle).
    private sealed class TextInputGuardCommand(ICommand inner) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object? parameter) =>
            Keyboard.FocusedElement is not (TextBox or PasswordBox or RichTextBox)
            && inner.CanExecute(parameter);

        public void Execute(object? parameter) => inner.Execute(parameter);
    }

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
            ICommand boundCmd = spec.Modifiers == ModifierKeys.None
                ? new TextInputGuardCommand(cmd)
                : cmd;
            var kb = new KeyBinding { Command = boundCmd, Key = spec.Key, Modifiers = spec.Modifiers };
            if (action.CommandParameter is not null)
                kb.CommandParameter = action.CommandParameter;
            window.InputBindings.Add(kb);
        }
    }
}
