namespace OmniPanel.Rendering;

/// <summary>Per-frame timing and skip counters surfaced to the CLI and GUI.</summary>
public sealed class RenderStats
{
    /// <summary>Wall-clock milliseconds spent producing the last frame.</summary>
    public double LastFrameMilliseconds { get; internal set; }

    /// <summary>Widgets skipped because they threw or failed to evaluate.</summary>
    public int SkippedWidgets { get; internal set; }

    /// <summary>Cumulative widgets skipped since the renderer was constructed.</summary>
    public int TotalSkippedWidgets { get; internal set; }

    /// <summary>Frames rendered since construction.</summary>
    public long FrameCount { get; internal set; }

    /// <summary>Moving average of recent frame times, in milliseconds.</summary>
    public double AverageFrameMilliseconds { get; internal set; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{LastFrameMilliseconds:0.00} ms | skipped {SkippedWidgets} | frames {FrameCount}";
}

/// <summary>
/// Rate-limited failure log so a broken widget does not spam once per frame.
/// </summary>
internal sealed class RenderDiagnostics
{
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public RenderStats Stats { get; } = new();

    public bool ShouldReport(string key)
    {
        lock (_gate)
        {
            return _reported.Add(key);
        }
    }

    public void NoteSkip()
    {
        Stats.SkippedWidgets++;
        Stats.TotalSkippedWidgets++;
    }

    public void BeginFrame()
    {
        Stats.SkippedWidgets = 0;
    }

    public void EndFrame(double milliseconds)
    {
        Stats.LastFrameMilliseconds = milliseconds;
        Stats.FrameCount++;
        // Exponential moving average keeps the number stable without storing a ring.
        const double alpha = 0.15d;
        Stats.AverageFrameMilliseconds = Stats.FrameCount == 1
            ? milliseconds
            : (alpha * milliseconds) + ((1d - alpha) * Stats.AverageFrameMilliseconds);
    }
}
