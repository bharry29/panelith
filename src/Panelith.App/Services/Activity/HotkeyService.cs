using System.Globalization;

namespace Panelith.App.Services.Activity;

/// <summary>A parsed global shortcut such as "Ctrl+Alt+Right".</summary>
/// <param name="Modifiers">MOD_* flags.</param>
/// <param name="VirtualKey">The Windows virtual-key code.</param>
/// <param name="Text">The shortcut written in its canonical form.</param>
public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Text)
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13,
        ["Comma"] = 0xBC, ["Period"] = 0xBE, ["Minus"] = 0xBD, ["Plus"] = 0xBB,
    };

    /// <summary>Parses a shortcut. At least one of Ctrl, Alt or Win is required so ordinary typing is never captured.</summary>
    public static Hotkey? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        uint modifiers = 0;
        uint? key = null;
        string? keyName = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= NativeFlags.Control;
                    continue;
                case "ALT":
                    modifiers |= NativeFlags.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= NativeFlags.Shift;
                    continue;
                case "WIN" or "WINDOWS" or "META":
                    modifiers |= NativeFlags.Win;
                    continue;
            }

            if (key is not null)
            {
                return null;
            }

            (key, keyName) = ParseKey(raw);
            if (key is null)
            {
                return null;
            }
        }

        if (key is null || (modifiers & (NativeFlags.Control | NativeFlags.Alt | NativeFlags.Win)) == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if ((modifiers & NativeFlags.Control) != 0)
        {
            parts.Add("Ctrl");
        }

        if ((modifiers & NativeFlags.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & NativeFlags.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & NativeFlags.Win) != 0)
        {
            parts.Add("Win");
        }

        parts.Add(keyName!);
        return new Hotkey(modifiers, key.Value, string.Join('+', parts));
    }

    private static (uint?, string?) ParseKey(string raw)
    {
        if (raw.Length == 1 && char.IsAsciiLetterOrDigit(raw[0]))
        {
            var c = char.ToUpperInvariant(raw[0]);
            return (c, c.ToString());
        }

        if (raw.Length is 2 or 3 && (raw[0] is 'F' or 'f') && int.TryParse(raw.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            return ((uint)(0x70 + f - 1), "F" + f.ToString(CultureInfo.InvariantCulture));
        }

        if (raw.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && raw.Length == 7 && char.IsAsciiDigit(raw[6]))
        {
            return ((uint)(0x60 + raw[6] - '0'), "NumPad" + raw[6]);
        }

        foreach (var (name, code) in NamedKeys)
        {
            if (string.Equals(name, raw, StringComparison.OrdinalIgnoreCase))
            {
                return (code, name);
            }
        }

        return (null, null);
    }

    private static class NativeFlags
    {
        public const uint Alt = 0x1;
        public const uint Control = 0x2;
        public const uint Shift = 0x4;
        public const uint Win = 0x8;
    }
}

/// <summary>
/// Registers system-wide shortcuts. They are owned by a background thread with
/// its own message loop, so they keep working while the window is hidden or was
/// never shown.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Thread? _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private IReadOnlyList<(string Action, Hotkey Key)> _wanted = [];
    private IReadOnlyList<string> _failed = [];

    public HotkeyService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Panelith hotkeys" };
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Raised on the hotkey thread with the action of the shortcut pressed.</summary>
    public event Action<string>? Pressed;

    /// <summary>Raised on the hotkey thread after registering, with the shortcuts Windows refused.</summary>
    public event Action<IReadOnlyList<string>>? Registered;

    /// <summary>Shortcuts another program already owns, from the last registration.</summary>
    public IReadOnlyList<string> Failed
    {
        get
        {
            lock (_gate)
            {
                return _failed;
            }
        }
    }

    /// <summary>Replaces every registered shortcut. Unparseable shortcuts are skipped.</summary>
    public void Set(IEnumerable<(string Action, string? Shortcut)> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        var parsed = new List<(string, Hotkey)>();
        var seen = new HashSet<(uint, uint)>();
        foreach (var (action, shortcut) in bindings)
        {
            if (Hotkey.TryParse(shortcut) is { } key && seen.Add((key.Modifiers, key.VirtualKey)))
            {
                parsed.Add((action, key));
            }
        }

        lock (_gate)
        {
            _wanted = parsed;
        }

        if (OperatingSystem.IsWindows() && _threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmApp, 0, 0);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (OperatingSystem.IsWindows() && _threadId != 0)
        {
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WmQuit, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(1));
        }

        _ready.Dispose();
    }

    private void Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Force the thread's message queue to exist before anyone posts to it.
        NativeMethods.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = NativeMethods.GetCurrentThreadId();
        _ready.Set();

        var registered = new List<(string Action, Hotkey Key)>();
        while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == NativeMethods.WmHotkey)
            {
                var index = (int)message.WParam - 1;
                if (index >= 0 && index < registered.Count)
                {
                    Pressed?.Invoke(registered[index].Action);
                }
            }
            else if (message.Message == NativeMethods.WmApp)
            {
                for (var i = 0; i < registered.Count; i++)
                {
                    NativeMethods.UnregisterHotKey(0, i + 1);
                }

                IReadOnlyList<(string Action, Hotkey Key)> wanted;
                lock (_gate)
                {
                    wanted = _wanted;
                }

                registered = [.. wanted];
                var failed = new List<string>();
                for (var i = 0; i < registered.Count; i++)
                {
                    var key = registered[i].Key;
                    if (!NativeMethods.RegisterHotKey(0, i + 1, key.Modifiers | NativeMethods.ModNoRepeat, key.VirtualKey))
                    {
                        failed.Add(key.Text);
                    }
                }

                lock (_gate)
                {
                    _failed = failed;
                }

                Registered?.Invoke(failed);
            }
        }

        for (var i = 0; i < registered.Count; i++)
        {
            NativeMethods.UnregisterHotKey(0, i + 1);
        }
    }
}
