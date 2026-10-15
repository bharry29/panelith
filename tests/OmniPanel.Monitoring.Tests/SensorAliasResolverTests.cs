using OmniPanel.Monitoring.Aliasing;
using OmniPanel.Sensors;
using Xunit;

namespace OmniPanel.Monitoring;

public sealed class SensorAliasResolverTests
{
    [Fact]
    public void CpuTempPrefersPackageOverCoreOverAcpi()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("acpi", "0", "temperature", "temp1"), "acpitz", SensorKind.Temperature, 40),
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "core_0"), "Core 0", SensorKind.Temperature, 55),
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "package"), "Package id 0", SensorKind.Temperature, 60),
        };

        var resolver = new SensorAliasResolver();
        var map = resolver.Resolve(readings);
        Assert.Equal("cpu/0/temperature/package", map[WellKnownSensors.CpuTemperature].Value);
    }

    [Fact]
    public void CpuTempFallsBackToAcpiWhenNoCpuSensor()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("acpi", "0", "temperature", "temp1"), "acpitz temp1", SensorKind.Temperature, 40),
        };

        var map = new SensorAliasResolver().Resolve(readings);
        Assert.Equal("acpi/0/temperature/temp1", map[WellKnownSensors.CpuTemperature].Value);
    }

    [Fact]
    public void GpuTempPrefersDiscreteOverIntegrated()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("gpu", "0", "temperature", "edge"), "Intel UHD Graphics", SensorKind.Temperature, 50),
            SensorReading.Numeric(SensorId.From("gpu", "1", "temperature", "edge"), "NVIDIA GeForce RTX 4070", SensorKind.Temperature, 65),
        };

        var map = new SensorAliasResolver().Resolve(readings);
        Assert.Equal("gpu/1/temperature/edge", map[WellKnownSensors.GpuTemperature].Value);
    }

    [Fact]
    public void NetworkAliasesPreferAggregateAndExcludeVirtual()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("network", "lo", "throughput", "down"), "lo Download", SensorKind.Throughput, 999),
            SensorReading.Numeric(SensorId.From("network", "docker0", "throughput", "down"), "docker0 Download", SensorKind.Throughput, 888),
            SensorReading.Numeric(SensorId.From("network", "eth0", "throughput", "down"), "eth0 Download", SensorKind.Throughput, 100),
            SensorReading.Numeric(SensorId.From("network", "0", "throughput", "down"), "Network Download", SensorKind.Throughput, 100),
            SensorReading.Numeric(SensorId.From("network", "0", "throughput", "up"), "Network Upload", SensorKind.Throughput, 50),
        };

        var map = new SensorAliasResolver().Resolve(readings);
        Assert.Equal("network/0/throughput/down", map[WellKnownSensors.NetworkDown].Value);
        Assert.Equal("network/0/throughput/up", map[WellKnownSensors.NetworkUp].Value);
    }

    [Fact]
    public void ResolutionIsDeterministicAcrossRepeatedCalls()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "core_1"), "Core 1", SensorKind.Temperature, 50),
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "core_0"), "Core 0", SensorKind.Temperature, 50),
        };

        var resolver = new SensorAliasResolver();
        var first = resolver.Resolve(readings)[WellKnownSensors.CpuTemperature];
        var second = resolver.Resolve(readings)[WellKnownSensors.CpuTemperature];
        Assert.Equal(first, second);
        // Tie on score → ordinal id wins.
        Assert.Equal("cpu/0/temperature/core_0", first.Value);
    }

    [Fact]
    public void UserOverrideWins()
    {
        var readings = new[]
        {
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "package"), "Package", SensorKind.Temperature, 60),
            SensorReading.Numeric(SensorId.From("cpu", "0", "temperature", "core_0"), "Core 0", SensorKind.Temperature, 55),
        };

        var resolver = new SensorAliasResolver();
        resolver.SetOverride(WellKnownSensors.CpuTemperature, SensorId.From("cpu", "0", "temperature", "core_0"));
        var map = resolver.Resolve(readings);
        Assert.Equal("cpu/0/temperature/core_0", map[WellKnownSensors.CpuTemperature].Value);
    }
}
