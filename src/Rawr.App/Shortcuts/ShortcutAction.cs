using System.Windows.Input;

namespace Rawr.App.Shortcuts;

public sealed record ShortcutAction(
    string Id,
    string DisplayName,
    string Category,
    Key DefaultKey,
    ModifierKeys DefaultModifiers,
    Func<MainWindow, ICommand?> ResolveCommand,
    object? CommandParameter = null)
{
    public KeySpec DefaultBinding => new(DefaultKey, DefaultModifiers);
}
