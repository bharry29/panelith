using System.Buffers.Binary;
using System.Text;
using Panelith.Monitoring.Providers.Rtss;
using Xunit;

namespace Panelith.Monitoring;

public sealed class RtssSharedMemoryTests
{
    private const int EntrySize = 512;
    private const int AppOffset = 64;

    [Fact]
    public void ParsesAppsFromHandBuiltMapping()
    {
        var buffer = new byte[AppOffset + (EntrySize * 3)];
        WriteHeader(buffer, count: 3);
        WriteEntry(buffer.AsSpan(AppOffset, EntrySize), 1234, @"C:\Games\Doom\doom.exe", time0: 10_000, time1: 11_000, frames: 144, frameTimeUs: 6_944);
        WriteEntry(buffer.AsSpan(AppOffset + (EntrySize * 2), EntrySize), 77, "obs64.exe", time0: 0, time1: 0, frames: 0, frameTimeUs: 0);

        var apps = RtssSharedMemory.ParseAll(buffer);

        Assert.Equal(2, apps.Count);
        var doom = apps[0];
        Assert.Equal(1234, doom.ProcessId);
        Assert.Equal("doom", doom.Name);
        Assert.Equal(144d, doom.FramesPerSecond!.Value, 3);
        Assert.Equal(6.944, doom.FrameTimeMilliseconds!.Value, 3);

        var idle = apps[1];
        Assert.Equal("obs64", idle.Name);
        Assert.Null(idle.FramesPerSecond);
        Assert.Null(idle.FrameTimeMilliseconds);
    }

    [Fact]
    public void RejectsForeignOrOldHeaders()
    {
        Assert.Null(RtssSharedMemory.ParseHeader([]));

        var buffer = new byte[AppOffset + EntrySize];
        WriteHeader(buffer, count: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xDEAD);
        Assert.Empty(RtssSharedMemory.ParseAll(buffer));

        WriteHeader(buffer, count: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 0x00010000);
        Assert.Null(RtssSharedMemory.ParseHeader(buffer));
    }

    [Fact]
    public void TruncatedMappingStopsAtLastWholeEntry()
    {
        var buffer = new byte[AppOffset + EntrySize + 100];
        WriteHeader(buffer, count: 2);
        WriteEntry(buffer.AsSpan(AppOffset, EntrySize), 5, "game.exe", 0, 1000, 60, 16_666);

        Assert.Single(RtssSharedMemory.ParseAll(buffer));
    }

    [Fact]
    public void OnePercentLowNeedsEnoughSamples()
    {
        Assert.Null(RtssSharedMemory.OnePercentLow([16.6, 16.6, 16.6]));
    }

    [Fact]
    public void OnePercentLowOfSteadyFramesIsTheFrameRate()
    {
        var samples = Enumerable.Repeat(10d, 200).ToArray();
        Assert.Equal(100d, RtssSharedMemory.OnePercentLow(samples)!.Value, 6);
    }

    [Fact]
    public void OnePercentLowCorrectsForTimeWeightedSampling()
    {
        // 1 000 frames at 10 ms and 5 hitches of 100 ms: the hitches are 0.5% of
        // frames but a third of wall time, so a timer samples them far more often.
        // Each 10 ms frame is sampled once, each 100 ms frame ten times.
        var samples = Enumerable.Repeat(10d, 1000).Concat(Enumerable.Repeat(100d, 50)).ToArray();

        var low = RtssSharedMemory.OnePercentLow(samples)!.Value;

        // Per frame, the 99th percentile is still a 10 ms frame. The naive
        // percentile over raw samples would wrongly report 10 FPS.
        Assert.Equal(100d, low, 6);
    }

    [Fact]
    public void OnePercentLowReportsFrequentHitches()
    {
        // 3% of frames take 50 ms, sampled in proportion to their duration.
        var samples = Enumerable.Repeat(10d, 970).Concat(Enumerable.Repeat(50d, 30 * 5)).ToArray();

        Assert.Equal(20d, RtssSharedMemory.OnePercentLow(samples)!.Value, 6);
    }

    private static void WriteHeader(Span<byte> buffer, int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, RtssSharedMemory.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..], 0x00020014);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[12..], AppOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[16..], (uint)count);
    }

    private static void WriteEntry(Span<byte> entry, int pid, string path, uint time0, uint time1, uint frames, uint frameTimeUs)
    {
        BinaryPrimitives.WriteInt32LittleEndian(entry, pid);
        Encoding.Latin1.GetBytes(path).CopyTo(entry[4..]);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[268..], time0);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[272..], time1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[276..], frames);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[280..], frameTimeUs);
    }
}
