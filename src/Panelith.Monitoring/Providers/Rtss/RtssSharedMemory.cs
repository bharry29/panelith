using System.Buffers.Binary;
using System.Text;

namespace Panelith.Monitoring.Providers.Rtss;

/// <summary>One program RivaTuner Statistics Server is measuring.</summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="Path">The executable path RTSS recorded.</param>
/// <param name="Time0">Start of the current averaging window, in milliseconds.</param>
/// <param name="Time1">End of the current averaging window, in milliseconds.</param>
/// <param name="Frames">Frames presented in that window.</param>
/// <param name="FrameTimeMicroseconds">How long the last frame took.</param>
public readonly record struct RtssApp(int ProcessId, string Path, uint Time0, uint Time1, uint Frames, uint FrameTimeMicroseconds)
{
    /// <summary>The executable name without folder or extension.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Average frames per second over the RTSS window, or null when the window is empty.</summary>
    public double? FramesPerSecond => Time1 > Time0 && Frames > 0 ? 1000d * Frames / (Time1 - Time0) : null;

    /// <summary>The last frame time in milliseconds, or null when RTSS has none.</summary>
    public double? FrameTimeMilliseconds => FrameTimeMicroseconds > 0 ? FrameTimeMicroseconds / 1000d : null;
}

/// <summary>
/// The layout of RTSS's <c>RTSSSharedMemoryV2</c> mapping, as published in the
/// RivaTuner SDK (<c>RTSSSharedMemory.h</c>).
/// </summary>
public static class RtssSharedMemory
{
    /// <summary>The mapping name.</summary>
    public const string MappingName = "RTSSSharedMemoryV2";

    /// <summary>The signature 'RTSS' as RTSS writes it, a multi-character constant read as a little-endian DWORD.</summary>
    public const uint Signature = 0x52545353;

    /// <summary>The oldest layout this reader understands.</summary>
    public const uint MinimumVersion = 0x00020000;

    /// <summary>Bytes of header read.</summary>
    public const int HeaderSize = 20;

    /// <summary>Bytes of each app entry read: up to and including the frame time.</summary>
    public const int EntryPrefixSize = 284;

    private const int NameOffset = 4;
    private const int NameLength = 260;
    private const int Time0Offset = 268;
    private const int Time1Offset = 272;
    private const int FramesOffset = 276;
    private const int FrameTimeOffset = 280;

    /// <summary>Where the app entries are, from the header, or null when the header is not RTSS's.</summary>
    public static (int Offset, int EntrySize, int Count)? ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(header) != Signature
            || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) < MinimumVersion)
        {
            return null;
        }

        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        if (entrySize < EntryPrefixSize || entrySize > 1 << 20 || offset < HeaderSize || offset > 1 << 24 || count > 4096)
        {
            return null;
        }

        return ((int)offset, (int)entrySize, (int)count);
    }

    /// <summary>Reads one app entry, or null for an empty slot.</summary>
    public static RtssApp? ParseEntry(ReadOnlySpan<byte> entry)
    {
        if (entry.Length < EntryPrefixSize)
        {
            return null;
        }

        var processId = BinaryPrimitives.ReadInt32LittleEndian(entry);
        if (processId <= 0)
        {
            return null;
        }

        var name = entry.Slice(NameOffset, NameLength);
        var end = name.IndexOf((byte)0);
        var path = Encoding.Latin1.GetString(end < 0 ? name : name[..end]);

        return new RtssApp(
            processId,
            path,
            BinaryPrimitives.ReadUInt32LittleEndian(entry[Time0Offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[Time1Offset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[FramesOffset..]),
            BinaryPrimitives.ReadUInt32LittleEndian(entry[FrameTimeOffset..]));
    }

    /// <summary>Reads every app in a complete copy of the mapping.</summary>
    public static IReadOnlyList<RtssApp> ParseAll(ReadOnlySpan<byte> memory)
    {
        if (ParseHeader(memory) is not { } layout)
        {
            return [];
        }

        var apps = new List<RtssApp>();
        for (var i = 0; i < layout.Count; i++)
        {
            var start = layout.Offset + (i * layout.EntrySize);
            if (start + EntryPrefixSize > memory.Length)
            {
                break;
            }

            if (ParseEntry(memory.Slice(start, EntryPrefixSize)) is { } app)
            {
                apps.Add(app);
            }
        }

        return apps;
    }

    /// <summary>
    /// The frame rate that 99% of frames beat, from frame times in milliseconds
    /// sampled at random moments; null with too few samples to mean anything.
    /// </summary>
    /// <remarks>
    /// Sampling the "last frame time" on a timer picks long frames more often
    /// than short ones, in proportion to how long they last. Weighting each
    /// sample by the inverse of its duration turns that back into a per-frame
    /// distribution, which is what a 1% low is defined over.
    /// </remarks>
    public static double? OnePercentLow(IReadOnlyCollection<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        var sorted = frameTimesMs.Where(t => t > 0).Order().ToArray();
        if (sorted.Length < 20)
        {
            return null;
        }

        var total = sorted.Sum(t => 1d / t);
        var threshold = total * 0.99;
        var cumulative = 0d;
        foreach (var time in sorted)
        {
            cumulative += 1d / time;
            if (cumulative >= threshold)
            {
                return 1000d / time;
            }
        }

        return 1000d / sorted[^1];
    }
}
