using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json.Serialization;

namespace RigGlass.Devices.Profiles;

/// <summary>The width and byte order of one header field.</summary>
public enum HeaderFieldType
{
    /// <summary>A single byte.</summary>
    U8 = 0,

    /// <summary>16-bit, little-endian.</summary>
    U16Le,

    /// <summary>16-bit, big-endian.</summary>
    U16Be,

    /// <summary>32-bit, little-endian.</summary>
    U32Le,

    /// <summary>32-bit, big-endian.</summary>
    U32Be,

    /// <summary>A run of literal bytes, given as a hex string.</summary>
    Bytes,

    /// <summary>A run of zero bytes, used for reserved gaps.</summary>
    Pad,
}

/// <summary>
/// One field of a declarative frame header.
/// </summary>
/// <param name="Type">The field's width and byte order.</param>
/// <param name="Value">
/// A decimal or <c>0x</c>-prefixed literal, a hex string for
/// <see cref="HeaderFieldType.Bytes"/>, or one of the placeholder names listed
/// on <see cref="HeaderTemplate"/>.
/// </param>
/// <param name="Count">
/// Byte count for <see cref="HeaderFieldType.Pad"/>, ignored otherwise.
/// </param>
public readonly record struct HeaderField(HeaderFieldType Type, string? Value = null, int Count = 1);

/// <summary>
/// Values a header field can refer to, resolved per frame.
/// </summary>
/// <param name="Width">Panel width in pixels.</param>
/// <param name="Height">Panel height in pixels.</param>
/// <param name="PayloadSize">Bytes of pixel or compressed data following the header.</param>
/// <param name="Command">The profile's command byte.</param>
/// <param name="ChunkIndex">Index of the current chunk, for chunked transports.</param>
/// <param name="ChunkCount">Total chunk count, for chunked transports.</param>
/// <param name="ChunkLength">Bytes of data in the current chunk.</param>
public readonly record struct HeaderContext(
    int Width,
    int Height,
    int PayloadSize,
    int Command = 0,
    int ChunkIndex = 0,
    int ChunkCount = 1,
    int ChunkLength = 0);

/// <summary>
/// A frame header described as data rather than code.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every panel family puts the same handful of numbers in front of its
/// pixel data -- resolution, payload size, a command byte -- and differs only in
/// the order, width and byte order of those fields. Describing that as data
/// means a new panel can be supported by writing a JSON profile, with no code
/// change, no rebuild and no release.
/// </para>
/// <para>
/// This is the mechanism behind <c>rigglass devices add</c>: a user with an
/// unsupported panel can describe it, test it, and share the profile as a file.
/// </para>
/// </remarks>
public sealed record HeaderTemplate
{
    /// <summary>The placeholder names a field value may use.</summary>
    public static IReadOnlySet<string> Placeholders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "width", "height", "payloadSize", "command", "chunkIndex", "chunkCount", "chunkLength",
    };

    /// <summary>Literal bytes written before the fields, as a hex string.</summary>
    public string? Prefix { get; init; }

    /// <summary>The fields, written in order.</summary>
    public IReadOnlyList<HeaderField> Fields { get; init; } = [];

    /// <summary>
    /// Pads the header out to this many bytes with zeros. Several firmwares
    /// expect a fixed-size header regardless of which fields are populated.
    /// </summary>
    public int? TotalSize { get; init; }

    /// <summary>A header with no bytes at all, for panels that take raw pixels.</summary>
    [JsonIgnore]
    public static HeaderTemplate None { get; } = new();

    /// <summary>Whether this template produces no bytes.</summary>
    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrEmpty(Prefix) && Fields.Count == 0 && TotalSize is null or 0;

    /// <summary>
    /// The number of bytes <see cref="Build(HeaderContext)"/> produces. Fixed
    /// for a given template, since no field is variable-length.
    /// </summary>
    public int Size
    {
        get
        {
            var size = ParseHex(Prefix).Length;
            foreach (var entry in Fields)
            {
                size += SizeOf(entry);
            }

            return TotalSize is { } total ? Math.Max(total, size) : size;
        }
    }

    /// <summary>Builds the header for one frame.</summary>
    /// <exception cref="PanelProfileException">A field value is not understood.</exception>
    public byte[] Build(HeaderContext context)
    {
        var header = new byte[Size];
        Build(context, header);
        return header;
    }

    /// <summary>Writes the header into <paramref name="destination"/>.</summary>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    /// <exception cref="PanelProfileException">A field value is not understood.</exception>
    public int Build(HeaderContext context, Span<byte> destination)
    {
        var size = Size;
        if (destination.Length < size)
        {
            throw new ArgumentException(
                $"This header is {size} bytes but the destination holds {destination.Length}.",
                nameof(destination));
        }

        var window = destination[..size];
        window.Clear();

        var offset = 0;
        var prefix = ParseHex(Prefix);
        prefix.CopyTo(window);
        offset += prefix.Length;

        foreach (var field in Fields)
        {
            offset += WriteField(field, context, window[offset..]);
        }

        return size;
    }

    private static int SizeOf(HeaderField field) => field.Type switch
    {
        HeaderFieldType.U8 => 1,
        HeaderFieldType.U16Le or HeaderFieldType.U16Be => 2,
        HeaderFieldType.U32Le or HeaderFieldType.U32Be => 4,
        HeaderFieldType.Bytes => ParseHex(field.Value).Length,
        HeaderFieldType.Pad => Math.Max(0, field.Count),
        _ => 0,
    };

    private static int WriteField(HeaderField field, HeaderContext context, Span<byte> destination)
    {
        switch (field.Type)
        {
            case HeaderFieldType.Pad:
                return Math.Max(0, field.Count);

            case HeaderFieldType.Bytes:
            {
                var bytes = ParseHex(field.Value);
                bytes.CopyTo(destination);
                return bytes.Length;
            }
        }

        var value = Resolve(field.Value, context);

        switch (field.Type)
        {
            case HeaderFieldType.U8:
                destination[0] = (byte)value;
                return 1;
            case HeaderFieldType.U16Le:
                BinaryPrimitives.WriteUInt16LittleEndian(destination, (ushort)value);
                return 2;
            case HeaderFieldType.U16Be:
                BinaryPrimitives.WriteUInt16BigEndian(destination, (ushort)value);
                return 2;
            case HeaderFieldType.U32Le:
                BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)value);
                return 4;
            case HeaderFieldType.U32Be:
                BinaryPrimitives.WriteUInt32BigEndian(destination, (uint)value);
                return 4;
            default:
                return 0;
        }
    }

    private static long Resolve(string? value, HeaderContext context)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var text = value.Trim();

        switch (text.ToLowerInvariant())
        {
            case "width": return context.Width;
            case "height": return context.Height;
            case "payloadsize": return context.PayloadSize;
            case "command": return context.Command;
            case "chunkindex": return context.ChunkIndex;
            case "chunkcount": return context.ChunkCount;
            case "chunklength": return context.ChunkLength;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
        {
            return hex;
        }

        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        throw new PanelProfileException(
            $"\"{text}\" is neither a number nor a known header placeholder. "
            + $"Valid placeholders are: {string.Join(", ", Placeholders.Order(StringComparer.Ordinal))}.");
    }

    /// <summary>
    /// Parses a hex string, tolerating spaces and <c>0x</c> prefixes so that
    /// profiles can be written as <c>"12 34 56 78"</c> or <c>"12345678"</c>.
    /// </summary>
    internal static byte[] ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        Span<char> digits = text.Length <= 256 ? stackalloc char[text.Length] : new char[text.Length];
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch is ' ' or '-' or '_' or ':' or ',')
            {
                continue;
            }

            if (ch is '0' && i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
            {
                i++;
                continue;
            }

            digits[length++] = ch;
        }

        if (length % 2 != 0)
        {
            throw new PanelProfileException($"\"{text}\" has an odd number of hex digits.");
        }

        var bytes = new byte[length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(digits.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
            {
                throw new PanelProfileException($"\"{text}\" is not a valid hex string.");
            }
        }

        return bytes;
    }
}

/// <summary>A device profile is malformed.</summary>
public sealed class PanelProfileException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PanelProfileException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
