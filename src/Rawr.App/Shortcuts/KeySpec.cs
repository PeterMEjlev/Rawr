using System.Text;
using System.Windows.Input;

namespace Rawr.App.Shortcuts;

public sealed record KeySpec(Key Key, ModifierKeys Modifiers)
{
    public static KeySpec? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries);
        var mods = ModifierKeys.None;
        Key? key = null;

        foreach (var rawPart in parts)
        {
            var p = rawPart.Trim();
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= ModifierKeys.Control; break;
                case "shift":   mods |= ModifierKeys.Shift;   break;
                case "alt":     mods |= ModifierKeys.Alt;     break;
                case "win":
                case "windows": mods |= ModifierKeys.Windows; break;
                case "0": key = Key.D0; break;
                case "1": key = Key.D1; break;
                case "2": key = Key.D2; break;
                case "3": key = Key.D3; break;
                case "4": key = Key.D4; break;
                case "5": key = Key.D5; break;
                case "6": key = Key.D6; break;
                case "7": key = Key.D7; break;
                case "8": key = Key.D8; break;
                case "9": key = Key.D9; break;
                default:
                    if (Enum.TryParse<Key>(p, true, out var k)) key = k;
                    break;
            }
        }
        return key.HasValue ? new KeySpec(key.Value, mods) : null;
    }

    public override string ToString() => Format(Key, Modifiers);

    public static string Format(Key key, ModifierKeys mods)
    {
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Shift))   sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Alt))     sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(KeyDisplayName(key));
        return sb.ToString();
    }

    public static string KeyDisplayName(Key key) => key switch
    {
        Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
        Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
        Key.OemPlus     => "=",
        Key.OemMinus    => "-",
        Key.OemComma    => ",",
        Key.OemPeriod   => ".",
        Key.OemQuestion => "/",
        Key.OemTilde    => "`",
        Key.OemSemicolon => ";",
        Key.OemQuotes   => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe     => "\\",
        _ => key.ToString()
    };

    public static bool IsModifierKey(Key k) =>
        k is Key.LeftCtrl or Key.RightCtrl
           or Key.LeftShift or Key.RightShift
           or Key.LeftAlt or Key.RightAlt
           or Key.LWin or Key.RWin
           or Key.System;
}
