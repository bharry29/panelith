using Panelith.Monitoring.IO;
using Panelith.Monitoring.Providers;
using Panelith.Sensors;
using Xunit;

namespace Panelith.Monitoring;

public sealed class LinuxSystemSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CpuLoadFirstSampleIsZeroNotGarbage()
    {
        var fs = BuildProc(
            stat: "cpu  100 0 100 800 0 0 0 0 0 0\ncpu0 50 0 50 400 0 0 0 0 0 0\n",
            meminfo: "MemTotal:       1048576 kB\nMemAvailable:    524288 kB\n",
            netDev: Header + "eth0: 1000 0 0 0 0 0 0 0 500 0 0 0 0 0 0 0\n");

        var source = new LinuxSystemSensorSource(fs, new FixedDriveProbe(), "/proc", "/sys");
        var first = await source.ReadAsync(Ct);
        var load = Assert.Single(first, r => r.Id.Value == "cpu/0/load/total");
        Assert.Equal(0d, load.Value);
    }

    [Fact]
    public async Task CpuLoadUsesDeltaBetweenSamples()
    {
        var fs = BuildProc(
            stat: "cpu  100 0 100 800 0 0 0 0 0 0\n",
            meminfo: "MemTotal:       1048576 kB\nMemAvailable:    524288 kB\n",
            netDev: Header + "eth0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");

        var source = new LinuxSystemSensorSource(fs, new FixedDriveProbe(), "/proc", "/sys");
        _ = await source.ReadAsync(Ct);

        // Advance: +100 user, +100 system, +200 idle → 50% busy.
        fs.AddFile("/proc/stat", "cpu  200 0 200 1000 0 0 0 0 0 0\n");
        await Task.Delay(20, Ct);
        var second = await source.ReadAsync(Ct);
        var load = Assert.Single(second, r => r.Id.Value == "cpu/0/load/total");
        Assert.Equal(50d, load.Value!.Value, precision: 1);
    }

    [Fact]
    public async Task NetworkWraparoundDoesNotProduceSpike()
    {
        var fs = BuildProc(
            stat: "cpu  0 0 0 100 0 0 0 0 0 0\n",
            meminfo: "MemTotal: 1024 kB\nMemAvailable: 512 kB\n",
            netDev: Header + "eth0: 4000000000 0 0 0 0 0 0 0 100 0 0 0 0 0 0 0\n");

        var source = new LinuxSystemSensorSource(fs, new FixedDriveProbe(), "/proc", "/sys");
        _ = await source.ReadAsync(Ct);

        // Counter reset / wrap: smaller than previous. Must not invent ~4 GB/s.
        fs.AddFile("/proc/net/dev", Header + "eth0: 1000 0 0 0 0 0 0 0 200 0 0 0 0 0 0 0\n");
        await Task.Delay(20, Ct);
        var second = await source.ReadAsync(Ct);
        var down = Assert.Single(second, r => r.Id.Value == "network/eth0/throughput/down");
        Assert.True(down.Value is null or 0d, $"unexpected rate {down.Value}");
    }

    [Fact]
    public async Task NetworkNormalDeltaComputesRate()
    {
        var fs = BuildProc(
            stat: "cpu  0 0 0 100 0 0 0 0 0 0\n",
            meminfo: "MemTotal: 1024 kB\nMemAvailable: 512 kB\n",
            netDev: Header + "eth0: 1000 0 0 0 0 0 0 0 500 0 0 0 0 0 0 0\n");

        var source = new LinuxSystemSensorSource(fs, new FixedDriveProbe(), "/proc", "/sys");
        _ = await source.ReadAsync(Ct);
        fs.AddFile("/proc/net/dev", Header + "eth0: 3000 0 0 0 0 0 0 0 1500 0 0 0 0 0 0 0\n");
        await Task.Delay(50, Ct);
        var second = await source.ReadAsync(Ct);
        var down = Assert.Single(second, r => r.Id.Value == "network/eth0/throughput/down");
        Assert.NotNull(down.Value);
        Assert.True(down.Value > 0d);
    }

    [Fact]
    public void RateFromCounterRejectsZeroElapsedAndDecrease()
    {
        Assert.Null(LinuxSystemSensorSource.RateFromCounter(10, 20, 0d));
        Assert.Null(LinuxSystemSensorSource.RateFromCounter(20, 10, 1d));
        Assert.Equal(10d, LinuxSystemSensorSource.RateFromCounter(10, 20, 1d));
    }

    [Fact]
    public async Task MemoryAndUptimeAndHostAreReported()
    {
        var fs = BuildProc(
            stat: "cpu  0 0 0 100 0 0 0 0 0 0\n",
            meminfo: "MemTotal:       2048000 kB\nMemAvailable:   1024000 kB\nSwapTotal: 1024000 kB\nSwapFree: 512000 kB\n",
            netDev: Header + "lo: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        fs.AddFile("/proc/uptime", "12345.67 1000.0\n");
        fs.AddFile("/proc/loadavg", "0.50 0.40 0.30 1/100 1\n");
        fs.AddFile("/proc/cpuinfo", "processor\t: 0\nmodel name\t: Test CPU\n");

        var source = new LinuxSystemSensorSource(fs, new FixedDriveProbe([new DriveVolume("root", 1000, 400)]), "/proc", "/sys");
        var readings = await source.ReadAsync(Ct);

        Assert.Equal(1024000d * 1024d, Assert.Single(readings, r => r.Id.Value == "memory/0/data/used").Value);
        Assert.Equal(12345.67d, Assert.Single(readings, r => r.Id.Value == "system/0/duration/uptime").Value);
        Assert.Equal("Test CPU", Assert.Single(readings, r => r.Id.Value == "cpu/0/name").Text);
        Assert.Contains(readings, r => r.Id.Value == "volume/0/load/used");
        // Loopback must not appear as a network interface reading.
        Assert.DoesNotContain(readings, r => r.Id.Value.Contains("/lo/", StringComparison.Ordinal));
    }

    [Fact]
    public void VirtualInterfaceDetection()
    {
        Assert.True(LinuxSystemSensorSource.IsVirtualInterface("lo"));
        Assert.True(LinuxSystemSensorSource.IsVirtualInterface("docker0"));
        Assert.True(LinuxSystemSensorSource.IsVirtualInterface("veth0a1b"));
        Assert.True(LinuxSystemSensorSource.IsVirtualInterface("br-abc"));
        Assert.False(LinuxSystemSensorSource.IsVirtualInterface("eth0"));
        Assert.False(LinuxSystemSensorSource.IsVirtualInterface("wlan0"));
    }

    private const string Header =
        "Inter-|   Receive                                                |  Transmit\n" +
        " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n";

    private static MemoryFileSystem BuildProc(string stat, string meminfo, string netDev)
    {
        return new MemoryFileSystem()
            .AddDirectory("/proc")
            .AddDirectory("/proc/net")
            .AddDirectory("/sys")
            .AddFile("/proc/stat", stat)
            .AddFile("/proc/meminfo", meminfo)
            .AddFile("/proc/net/dev", netDev);
    }

    private sealed class FixedDriveProbe : IDriveProbe
    {
        private readonly IReadOnlyList<DriveVolume> _volumes;

        public FixedDriveProbe(IReadOnlyList<DriveVolume>? volumes = null) =>
            _volumes = volumes ?? [];

        public IReadOnlyList<DriveVolume> GetVolumes() => _volumes;
    }
}
