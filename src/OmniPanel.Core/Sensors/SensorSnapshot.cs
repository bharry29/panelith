using System.Collections;
using System.Collections.Frozen;

namespace OmniPanel.Sensors;

/// <summary>
/// An immutable view of every sensor reading at one instant.
/// </summary>
/// <remarks>
/// The render pipeline treats a snapshot as the single source of truth for one
/// frame, so that a frame can never show two sensors sampled at different
/// times. Snapshots are cheap to pass around and safe to share across threads.
/// </remarks>
public sealed class SensorSnapshot : IReadOnlyCollection<SensorReading>
{
    private readonly FrozenDictionary<SensorId, SensorReading> _readings;

    private SensorSnapshot(DateTimeOffset timestamp, FrozenDictionary<SensorId, SensorReading> readings)
    {
        Timestamp = timestamp;
        _readings = readings;
    }

    /// <summary>An empty snapshot, timestamped at the Unix epoch.</summary>
    public static SensorSnapshot Empty { get; } =
        new(DateTimeOffset.UnixEpoch, FrozenDictionary<SensorId, SensorReading>.Empty);

    /// <summary>When the readings were sampled.</summary>
    public DateTimeOffset Timestamp { get; }

    /// <inheritdoc />
    public int Count => _readings.Count;

    /// <summary>Every sensor id present in this snapshot.</summary>
    public IReadOnlyCollection<SensorId> Ids => _readings.Keys;

    /// <summary>
    /// Builds a snapshot. When several readings share an id, the first one wins
    /// and the rest are discarded.
    /// </summary>
    /// <remarks>
    /// Duplicate ids are a source bug rather than a user-facing error -- for
    /// example two identically-labelled fan headers on one controller -- so the
    /// snapshot stays usable instead of throwing mid-render. Sources are
    /// expected to disambiguate with an instance index; see
    /// <see cref="SensorIdDeduplicator"/>.
    /// </remarks>
    public static SensorSnapshot Create(DateTimeOffset timestamp, IEnumerable<SensorReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var map = new Dictionary<SensorId, SensorReading>();
        foreach (var reading in readings)
        {
            if (reading.Id.IsEmpty)
            {
                continue;
            }

            map.TryAdd(reading.Id, reading);
        }

        return new SensorSnapshot(timestamp, map.ToFrozenDictionary());
    }

    /// <summary>Looks up a reading by id.</summary>
    public bool TryGet(SensorId id, out SensorReading reading) => _readings.TryGetValue(id, out reading);

    /// <summary>The reading for <paramref name="id"/>, or <see langword="null"/> when absent.</summary>
    public SensorReading? this[SensorId id] => _readings.TryGetValue(id, out var r) ? r : null;

    /// <summary>Whether <paramref name="id"/> is present.</summary>
    public bool Contains(SensorId id) => _readings.ContainsKey(id);

    /// <summary>Every reading whose id sits underneath <paramref name="prefix"/>.</summary>
    public IEnumerable<SensorReading> Under(SensorId prefix) =>
        _readings.Values.Where(r => r.Id.StartsWith(prefix));

    /// <summary>
    /// Produces a new snapshot with <paramref name="additional"/> merged in.
    /// Readings in <paramref name="additional"/> replace existing ids.
    /// </summary>
    public SensorSnapshot With(DateTimeOffset timestamp, IEnumerable<SensorReading> additional)
    {
        ArgumentNullException.ThrowIfNull(additional);

        var map = new Dictionary<SensorId, SensorReading>(_readings);
        foreach (var reading in additional)
        {
            if (!reading.Id.IsEmpty)
            {
                map[reading.Id] = reading;
            }
        }

        return new SensorSnapshot(timestamp, map.ToFrozenDictionary());
    }

    /// <inheritdoc />
    public IEnumerator<SensorReading> GetEnumerator() => _readings.Values.AsEnumerable().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
