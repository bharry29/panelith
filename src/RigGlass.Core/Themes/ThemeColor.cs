using System.Globalization;

namespace RigGlass.Themes;

/// <summary>A straight (non-premultiplied) 8-bit RGBA colour.</summary>
/// <param name="R">Red channel.</param>
/// <param name="G">Green channel.</param>
/// <param name="B">Blue channel.</param>
/// <param name="A">Alpha channel, 255 being opaque.</param>
public readonly record struct ThemeColor(byte R, byte G, byte B, byte A = 255)
{
    /// <summary>Fully transparent.</summary>
    public static ThemeColor Transparent { get; } = new(0, 0, 0, 0);

    /// <summary>Opaque black.</summary>
    public static ThemeColor Black { get; } = new(0, 0, 0);

    /// <summary>Opaque white.</summary>
    public static ThemeColor White { get; } = new(255, 255, 255);

    /// <summary>Whether this colour would draw nothing.</summary>
    public bool IsTransparent => A == 0;

    /// <summary>This colour packed as <c>0xAARRGGBB</c>.</summary>
    public uint ToArgb() => ((uint)A << 24) | ((uint)R << 16) | ((uint)G << 8) | B;

    /// <summary>Returns this colour with its alpha multiplied by <paramref name="factor"/>.</summary>
    public ThemeColor WithOpacity(double factor) =>
        this with { A = (byte)Math.Clamp(Math.Round(A * factor), 0d, 255d) };

    /// <summary>Linearly interpolates towards <paramref name="other"/>.</summary>
    public ThemeColor LerpTo(ThemeColor other, double t)
    {
        t = Math.Clamp(t, 0d, 1d);
        static byte Mix(byte a, byte b, double amount) => (byte)Math.Round(a + ((b - a) * amount));
        return new ThemeColor(Mix(R, other.R, t), Mix(G, other.G, t), Mix(B, other.B, t), Mix(A, other.A, t));
    }

    /// <summary>Renders this colour as <c>#rrggbb</c>, or <c>#rrggbbaa</c> when not opaque.</summary>
    public override string ToString() =>
        A == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}{A:x2}");

    /// <summary>
    /// Parses a colour written as <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>,
    /// <c>#rrggbbaa</c>, <c>rgb(r,g,b)</c>, <c>rgba(r,g,b,a)</c>, or one of the
    /// named colours.
    /// </summary>
    public static bool TryParse(string? text, out ThemeColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        if (NamedColors.TryGetValue(value, out color))
        {
            return true;
        }

        if (value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseFunctional(value, out color);
        }

        if (value[0] != '#')
        {
            return false;
        }

        var hex = value[1..];
        switch (hex.Length)
        {
            case 3 or 4:
            {
                Span<byte> channels = stackalloc byte[4] { 0, 0, 0, 255 };
                for (var i = 0; i < hex.Length; i++)
                {
                    if (!TryParseNibble(hex[i], out var nibble))
                    {
                        return false;
                    }

                    // #abc expands to #aabbcc, matching CSS.
                    channels[i] = (byte)((nibble << 4) | nibble);
                }

                color = new ThemeColor(channels[0], channels[1], channels[2], channels[3]);
                return true;
            }

            case 6 or 8:
            {
                Span<byte> channels = stackalloc byte[4] { 0, 0, 0, 255 };
                for (var i = 0; i < hex.Length / 2; i++)
                {
                    if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
                    {
                        return false;
                    }

                    channels[i] = parsed;
                }

                color = new ThemeColor(channels[0], channels[1], channels[2], channels[3]);
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>Parses a colour, falling back to <paramref name="fallback"/> when the text is invalid.</summary>
    public static ThemeColor ParseOrDefault(string? text, ThemeColor fallback) =>
        TryParse(text, out var color) ? color : fallback;

    private static bool TryParseFunctional(string value, out ThemeColor color)
    {
        color = default;
        var open = value.IndexOf('(');
        var close = value.LastIndexOf(')');
        if (open < 0 || close <= open)
        {
            return false;
        }

        var parts = value[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        Span<byte> channels = stackalloc byte[4] { 0, 0, 0, 255 };
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var component))
            {
                return false;
            }

            channels[i] = (byte)Math.Clamp(Math.Round(component), 0d, 255d);
        }

        if (parts.Length == 4)
        {
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
            {
                return false;
            }

            // CSS writes alpha as a 0-1 fraction; accept 0-255 too, since theme
            // authors coming from other tools write it either way.
            channels[3] = alpha <= 1d
                ? (byte)Math.Clamp(Math.Round(alpha * 255d), 0d, 255d)
                : (byte)Math.Clamp(Math.Round(alpha), 0d, 255d);
        }

        color = new ThemeColor(channels[0], channels[1], channels[2], channels[3]);
        return true;
    }

    private static bool TryParseNibble(char ch, out int value)
    {
        value = ch switch
        {
            >= '0' and <= '9' => ch - '0',
            >= 'a' and <= 'f' => ch - 'a' + 10,
            >= 'A' and <= 'F' => ch - 'A' + 10,
            _ => -1,
        };

        return value >= 0;
    }

    /// <summary>
    /// The named colours a theme may use. Deliberately a small, panel-oriented
    /// palette rather than the full CSS list.
    /// </summary>
    public static IReadOnlyDictionary<string, ThemeColor> NamedColors { get; } =
        new Dictionary<string, ThemeColor>(StringComparer.OrdinalIgnoreCase)
        {
            ["transparent"] = Transparent,
            ["black"] = Black,
            ["white"] = White,
            ["red"] = new(239, 68, 68),
            ["orange"] = new(249, 115, 22),
            ["amber"] = new(245, 158, 11),
            ["yellow"] = new(234, 179, 8),
            ["lime"] = new(132, 204, 22),
            ["green"] = new(34, 197, 94),
            ["teal"] = new(20, 184, 166),
            ["cyan"] = new(34, 211, 238),
            ["blue"] = new(59, 130, 246),
            ["indigo"] = new(99, 102, 241),
            ["violet"] = new(139, 92, 246),
            ["magenta"] = new(217, 70, 239),
            ["pink"] = new(236, 72, 153),
            ["gray"] = new(107, 114, 128),
            ["grey"] = new(107, 114, 128),
            ["slate"] = new(100, 116, 139),
            ["silver"] = new(192, 192, 192),
        };
}
