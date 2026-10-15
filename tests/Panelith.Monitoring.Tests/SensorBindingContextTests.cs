using Panelith.Expressions;
using Panelith.Monitoring.Aliasing;
using Panelith.Monitoring.Binding;
using Panelith.Monitoring.Hub;
using Panelith.Monitoring.Providers;
using Panelith.Sensors;
using Xunit;

namespace Panelith.Monitoring;

public sealed class SensorBindingContextTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void MissingSensorsYieldMissing()
    {
        var snapshot = SensorSnapshot.Empty;
        var context = new SensorBindingContext(
            () => snapshot,
            () => new Dictionary<string, SensorId>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(BindingValueKind.Missing, context.ResolveSensor(WellKnownSensors.GpuTemperature).Kind);
        Assert.Equal(BindingValueKind.Missing, context.ResolveSensor("cpu/0/temperature/package").Kind);
        Assert.Equal(BindingValueKind.Missing, context.ResolveVariable("accent").Kind);
    }

    [Fact]
    public void AliasesAndRawIdsResolve()
    {
        var package = SensorReading.Numeric(
            SensorId.From("cpu", "0", "temperature", "package"),
            "Package",
            SensorKind.Temperature,
            61);
        var snapshot = SensorSnapshot.Create(DateTimeOffset.UtcNow, [package]);
        var aliases = new Dictionary<string, SensorId>(StringComparer.OrdinalIgnoreCase)
        {
            [WellKnownSensors.CpuTemperature] = package.Id,
        };
        var variables = new Dictionary<string, BindingValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["accent"] = BindingValue.FromText("#ff0000"),
        };

        var context = new SensorBindingContext(() => snapshot, () => aliases, variables: variables);
        Assert.Equal(61d, context.ResolveSensor(WellKnownSensors.CpuTemperature).Number);
        Assert.Equal(61d, context.ResolveSensor("cpu/0/temperature/package").Number);
        Assert.Equal("#ff0000", context.ResolveVariable("accent").Text);
    }

    [Fact]
    public async Task HistoryReachesExpressionFunctions()
    {
        var source = new SyntheticSensorSource(seed: 99);
        var resolver = new SensorAliasResolver();
        await using var hub = new SensorHub(
            [source],
            resolver,
            new SensorHubOptions { HistorySamplesPerSensor = 128 });

        for (var i = 0; i < 5; i++)
        {
            await hub.PollOnceAsync(Ct);
        }

        var context = new SensorBindingContext(hub);
        var history = context.ResolveHistory(WellKnownSensors.CpuTemperature, TimeSpan.FromMinutes(1));
        Assert.True(history.Count >= 5);

        var avg = ExpressionFunctions.Invoke(
            "avgover",
            [new SensorNode(WellKnownSensors.CpuTemperature), new NumberNode(60)],
            context);
        Assert.Equal(BindingValueKind.Number, avg.Kind);
    }
}
