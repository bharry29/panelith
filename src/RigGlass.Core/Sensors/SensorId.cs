using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace RigGlass.Sensors;

/// <summary>
/// A stable, human-readable identifier for a single sensor, shaped like a path:
/// <c>cpu/0/temperature/package</c>.
/// </summary>
/// <remarks>
/// <para>
/// Themes bind to sensors by id, so ids must stay stable across application
/// restarts, across driver versions and -- as far as the host hardware allows --
/// across reboots. Sources therefore build ids from durable facts (hardware
/// class, instance index, measured quantity, sensor label) rather than from
/// enumeration order.
/// </para>
/// <para>
/// Ids are compared case-insensitively and are always stored lowercase, because
/// vendors are inconsistent about the casing of sensor labels.
/// </para>
/// </remarks>
public readonly record struct SensorId : IComparable<SensorId>
{
    /// <summary>Separator between the segments of an id.</summary>
    public const char Separator = '/';

    private readonly string? _value;

    private SensorId(string value) => _value = value;

    /// <summary>The canonical string form of the id, or an empty string when unset.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>Whether this id has a value.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>
    /// Builds an id from its segments, normalising each one.
    /// </summary>
    /// <param name="segments">
    /// Segments, outermost first. Empty or whitespace-only segments are dropped.
    /// </param>
    /// <exception cref="ArgumentException">All segments normalised away to nothing.</exception>
    public static SensorId From(params ReadOnlySpan<string?> segments)
    {
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            var normalized = NormalizeSegment(segment);
            if (normalized.Length == 0)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(Separator);
            }

            builder.Append(normalized);
        }

        if (builder.Length == 0)
        {
            throw new ArgumentException("A sensor id needs at least one non-empty segment.", nameof(segments));
        }

        return new SensorId(builder.ToString());
    }

    /// <summary>Parses an already-formed id string.</summary>
    /// <exception cref="ArgumentException">The string contains no usable segments.</exception>
    public static SensorId Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return From(value.Split(Separator));
    }

    /// <summary>Parses an id string, returning <see langword="false"/> instead of throwing.</summary>
    public static bool TryParse(string? value, out SensorId id)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            id = default;
            return false;
        }

        try
        {
            id = Parse(value);
            return true;
        }
        catch (ArgumentException)
        {
            id = default;
            return false;
        }
    }

    /// <summary>The segments of this id.</summary>
    public string[] Segments => IsEmpty ? [] : Value.Split(Separator);

    /// <summary>
    /// Appends further segments, producing a child id.
    /// </summary>
    public SensorId Child(params ReadOnlySpan<string?> segments)
    {
        if (IsEmpty)
        {
            return From(segments);
        }

        var combined = new string?[segments.Length + 1];
        combined[0] = Value;
        for (var i = 0; i < segments.Length; i++)
        {
            combined[i + 1] = segments[i];
        }

        return From(combined);
    }

    /// <summary>Whether this id sits underneath <paramref name="prefix"/> in the hierarchy.</summary>
    public bool StartsWith(SensorId prefix) =>
        !prefix.IsEmpty
        && !IsEmpty
        && (Value.Equals(prefix.Value, StringComparison.Ordinal)
            || Value.StartsWith(prefix.Value + Separator, StringComparison.Ordinal));

    /// <summary>
    /// Lowercases a segment and replaces every character that is not a letter,
    /// digit, <c>.</c>, <c>-</c> or <c>+</c> with <c>_</c>, collapsing runs of
    /// <c>_</c> and trimming them from both ends.
    /// </summary>
    /// <remarks>
    /// Vendor sensor labels contain spaces, <c>#</c>, <c>%</c>, parentheses and
    /// non-ASCII characters. Normalising keeps ids usable inside theme binding
    /// expressions, file names and command-line arguments. <c>+</c> survives
    /// because it carries meaning in labels such as <c>vcore+</c>, and <c>.</c>
    /// because of labels such as <c>3.3v</c>.
    /// </remarks>
    public static string NormalizeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(segment.Length);
        var pendingUnderscore = false;
        foreach (var ch in segment)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '+')
            {
                if (pendingUnderscore && builder.Length > 0)
                {
                    builder.Append('_');
                }

                pendingUnderscore = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingUnderscore = true;
            }
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public bool Equals(SensorId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(SensorId other) =>
        string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Implicit conversion to the canonical string form.</summary>
    public static implicit operator string(SensorId id) => id.Value;

    /// <summary>Whether <paramref name="value"/> is a well-formed id.</summary>
    public static bool IsValid([NotNullWhen(true)] string? value) => TryParse(value, out _);
}
