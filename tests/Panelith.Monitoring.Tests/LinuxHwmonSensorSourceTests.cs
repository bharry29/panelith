using Panelith.Monitoring.IO;
using Panelith.Monitoring.Providers;
using Panelith.Sensors;
using Xunit;

namespace Panelith.Monitoring;

public sealed class LinuxHwmonSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ParsesTemperaturesFansVoltagesAndPower()
    {
        var fs = new MemoryFileSystem()
            .AddDirectory("/sys/class/hwmon")
            .AddDirectory("/sys/class/hwmon/hwmon0")
            .AddFile("/sys/class/hwmon/hwmon0/name", "coretemp\n")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_input", "45000\n")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_label", "Package id 0\n")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_crit", "100000\n")
            .AddFile("/sys/class/hwmon/hwmon0/temp2_input", "42000\n")
            .AddFile("/sys/class/hwmon/hwmon0/temp2_label", "Core 0\n")
            .AddDirectory("/sys/class/hwmon/hwmon1")
            .AddFile("/sys/class/hwmon/hwmon1/name", "nct6775\n")
            .AddFile("/sys/class/hwmon/hwmon1/fan1_input", "1200\n")
            .AddFile("/sys/class/hwmon/hwmon1/fan1_label", "CPU Fan\n")
            .AddFile("/sys/class/hwmon/hwmon1/in0_input", "1056\n")
            .AddFile("/sys/class/hwmon/hwmon1/in0_label", "Vcore\n")
            .AddFile("/sys/class/hwmon/hwmon1/power1_input", "45000000\n")
            .AddFile("/sys/class/hwmon/hwmon1/power1_label", "Package\n");

        var source = new LinuxHwmonSensorSource(fs, "/sys/class/hwmon");
        Assert.True(source.IsAvailable);

        var readings = await source.ReadAsync(Ct);
        var package = Assert.Single(readings, r => r.DisplayName.Contains("Package id 0", StringComparison.Ordinal));
        Assert.Equal(45d, package.Value);
        Assert.Equal(SensorKind.Temperature, package.Kind);
        Assert.Equal(100d, package.Maximum);

        var fan = Assert.Single(readings, r => r.Kind == SensorKind.Fan);
        Assert.Equal(1200d, fan.Value);

        var voltage = Assert.Single(readings, r => r.Kind == SensorKind.Voltage);
        Assert.Equal(1.056d, voltage.Value!.Value, precision: 3);

        var power = Assert.Single(readings, r => r.Kind == SensorKind.Power);
        Assert.Equal(45d, power.Value);
    }

    [Fact]
    public async Task SkipsMissingEmptyAndNegativeInputs()
    {
        var fs = new MemoryFileSystem()
            .AddDirectory("/sys/class/hwmon/hwmon0")
            .AddFile("/sys/class/hwmon/hwmon0/name", "coretemp")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_input", "")
            .AddFile("/sys/class/hwmon/hwmon0/temp2_input", "-1")
            .AddFile("/sys/class/hwmon/hwmon0/temp3_input", "not-a-number")
            .AddFile("/sys/class/hwmon/hwmon0/temp4_input", "39000");

        var source = new LinuxHwmonSensorSource(fs, "/sys/class/hwmon");
        var readings = await source.ReadAsync(Ct);
        Assert.Single(readings);
        Assert.Equal(39d, readings[0].Value);
    }

    [Fact]
    public async Task DisambiguatesIdenticalChipNames()
    {
        var fs = new MemoryFileSystem()
            .AddDirectory("/sys/class/hwmon/hwmon0")
            .AddFile("/sys/class/hwmon/hwmon0/name", "nvme")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_input", "40000")
            .AddFile("/sys/class/hwmon/hwmon0/temp1_label", "Composite")
            .AddDirectory("/sys/class/hwmon/hwmon1")
            .AddFile("/sys/class/hwmon/hwmon1/name", "nvme")
            .AddFile("/sys/class/hwmon/hwmon1/temp1_input", "41000")
            .AddFile("/sys/class/hwmon/hwmon1/temp1_label", "Composite");

        var source = new LinuxHwmonSensorSource(fs, "/sys/class/hwmon");
        var readings = await source.ReadAsync(Ct);
        Assert.Equal(2, readings.Count);
        Assert.Equal(2, readings.Select(r => r.Id.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.All(readings, r => Assert.StartsWith("disk/", r.Id.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnavailableWhenRootMissing()
    {
        var source = new LinuxHwmonSensorSource(new MemoryFileSystem(), "/sys/class/hwmon");
        Assert.False(source.IsAvailable);
        Assert.Empty(await source.ReadAsync(Ct));
    }
}
