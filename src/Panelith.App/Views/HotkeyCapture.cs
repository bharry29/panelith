using Avalonia.Controls;
using Avalonia.Input;

namespace Panelith.App.Views;

/// <summary>Turns a key press in a shortcut box into text such as "Ctrl+Alt+1".</summary>
internal static class HotkeyCapture
{
    /// <summary>Handles a key press on a shortcut text box: records a chord, or clears on Backspace or Delete.</summary>
    public static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || e.Key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Back or Key.Delete or Key.Escape)
        {
            box.Text = string.Empty;
            return;
        }

        if (KeyName(e.Key) is not { } key)
        {
            return;
        }

        var parts = new List<string>();
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            parts.Add("Win");
        }

        if (parts.Count == 0 || (parts.Count == 1 && parts[0] == "Shift"))
        {
            return;
        }

        parts.Add(key);
        box.Text = string.Join('+', parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => "NumPad" + (int)(key - Key.NumPad0),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Space => "Space",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Home => "Home",
        Key.End => "End",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Pause => "Pause",
        _ => null,
    };
}
