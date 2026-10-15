namespace OmniPanel.Sensors;

/// <summary>
/// Hands out unique sensor ids, appending <c>_2</c>, <c>_3</c> and so on when a
/// source would otherwise emit the same id twice.
/// </summary>
/// <remarks>
/// Real hardware routinely reports duplicate labels: two fan headers both called
/// "Fan", several identical NVMe drives, or a motherboard that exposes the same
/// voltage rail under one name on two chips. Silently dropping the duplicates
/// loses sensors the user can see in other tools, and throwing would take the
/// whole source down, so instead the id gets a numeric suffix. The suffix is
/// assigned in enumeration order, which is stable for every backend OmniPanel
/// ships.
/// </remarks>
public sealed class SensorIdDeduplicator
{
    private readonly Dictionary<SensorId, int> _seen = [];

    /// <summary>
    /// Returns <paramref name="candidate"/> the first time it is seen, and a
    /// suffixed variant on every later call.
    /// </summary>
    public SensorId Unique(SensorId candidate)
    {
        if (candidate.IsEmpty)
        {
            return candidate;
        }

        ref var count = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(
            _seen, candidate, out var existed);
        count++;

        if (!existed)
        {
            return candidate;
        }

        // The suffixed id must itself be unique: a source could legitimately
        // report "fan" twice and also report a literal "fan_2".
        var attempt = count;
        while (true)
        {
            var suffixed = SensorId.Parse($"{candidate.Value}_{attempt}");
            if (!_seen.ContainsKey(suffixed))
            {
                _seen[suffixed] = 1;
                return suffixed;
            }

            attempt++;
        }
    }

    /// <summary>Forgets every id handed out so far.</summary>
    public void Reset() => _seen.Clear();
}
