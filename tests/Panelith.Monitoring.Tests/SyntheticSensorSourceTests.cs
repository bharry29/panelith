using Panelith.Monitoring.Providers;
using Panelith.Sensors;
using Xunit;

namespace Panelith.Monitoring;

public sealed class SyntheticSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FixedSeedIsReproducible()
    {
        var a = new SyntheticSensorSource(seed: 42);
        var b = new SyntheticSensorSource(seed: 42);

        var firstA = await a.ReadAsync(Ct);
        var firstB = await b.ReadAsync(Ct);
        Assert.Equal(firstA.Select(r => (r.Id.Value, r.Value, r.Text)), firstB.Select(r => (r.Id.Value, r.Value, r.Text)));

        var secondA = await a.ReadAsync(Ct);
        var secondB = await b.ReadAsync(Ct);
        Assert.Equal(secondA.Select(r => (r.Id.Value, r.Value, r.Text)), secondB.Select(r => (r.Id.Value, r.Value, r.Text)));
        Assert.NotEqual(firstA[0].Value, secondA[0].Value);
    }

    [Fact]
    public async Task CoversEveryWellKnownAlias()
    {
        var source = new SyntheticSensorSource(seed: 7);
        var readings = await source.ReadAsync(Ct);
        Assert.Equal(WellKnownSensors.All.Count, readings.Count);

        foreach (var alias in WellKnownSensors.All.Keys)
        {
            var expectedId = SyntheticSensorSource.IdForAlias(alias);
            Assert.Contains(readings, r => r.Id == expectedId);
        }
    }
}
