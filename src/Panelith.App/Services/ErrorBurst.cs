namespace Panelith.App.Services;

/// <summary>Notices when errors arrive in a burst: <c>count</c> of them within <c>window</c>.</summary>
public sealed class ErrorBurst(int count, TimeSpan window)
{
    private readonly Queue<DateTime> _times = new();
    private readonly Lock _gate = new();

    /// <summary>Records an error at <paramref name="now"/>. Returns true when it completes a burst.</summary>
    public bool Record(DateTime now)
    {
        lock (_gate)
        {
            _times.Enqueue(now);
            while (_times.Count > 0 && now - _times.Peek() > window)
            {
                _times.Dequeue();
            }

            if (_times.Count < count)
            {
                return false;
            }

            _times.Clear();
            return true;
        }
    }
}
