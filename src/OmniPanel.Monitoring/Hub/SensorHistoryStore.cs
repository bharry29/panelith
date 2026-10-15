using System.Collections.Concurrent;
using OmniPanel.Sensors;

namespace OmniPanel.Monitoring.Hub;

/// <summary>
/// Bounded per-sensor ring buffers of numeric samples. Written by the poll
/// thread and read by the render thread; individual buffers are locked briefly,
/// but the hot path never waits on a provider.
/// </summary>
internal sealed class SensorHistoryStore
{
    private readonly ConcurrentDictionary<string, RingBuffer> _buffers =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly int _capacity;

    public SensorHistoryStore(int capacity, TimeSpan retention)
    {
        _capacity = Math.Max(8, capacity);
        _ = retention;
    }

    public void Record(string key, DateTimeOffset timestamp, double value)
    {
        var buffer = _buffers.GetOrAdd(key, _ => new RingBuffer(_capacity));
        buffer.Add(timestamp, value);
    }

    public void RecordSnapshot(
        SensorSnapshot snapshot,
        IReadOnlyDictionary<string, SensorId> aliases,
        DateTimeOffset timestamp)
    {
        foreach (var reading in snapshot)
        {
            if (reading.Value is not { } value || double.IsNaN(value))
            {
                continue;
            }

            Record(reading.Id.Value, timestamp, value);
        }

        foreach (var (alias, target) in aliases)
        {
            if (snapshot.TryGet(target, out var reading) && reading.Value is { } value && !double.IsNaN(value))
            {
                Record(alias, timestamp, value);
            }
        }
    }

    public IReadOnlyList<double> Query(string reference, TimeSpan window, DateTimeOffset now)
    {
        if (!_buffers.TryGetValue(reference, out var buffer))
        {
            return [];
        }

        return buffer.Query(now - window, now);
    }

    private sealed class RingBuffer
    {
        private readonly (DateTimeOffset Timestamp, double Value)[] _items;
        private int _next;
        private int _count;
        private readonly object _gate = new();

        public RingBuffer(int capacity) => _items = new (DateTimeOffset, double)[capacity];

        public void Add(DateTimeOffset timestamp, double value)
        {
            lock (_gate)
            {
                _items[_next] = (timestamp, value);
                _next = (_next + 1) % _items.Length;
                if (_count < _items.Length)
                {
                    _count++;
                }
            }
        }

        public IReadOnlyList<double> Query(DateTimeOffset from, DateTimeOffset to)
        {
            lock (_gate)
            {
                var result = new List<double>(_count);
                var start = _count < _items.Length ? 0 : _next;
                for (var i = 0; i < _count; i++)
                {
                    var sample = _items[(start + i) % _items.Length];
                    if (sample.Timestamp >= from && sample.Timestamp <= to)
                    {
                        result.Add(sample.Value);
                    }
                }

                return result;
            }
        }
    }
}
