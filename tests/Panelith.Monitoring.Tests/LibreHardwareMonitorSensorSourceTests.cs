using Panelith.Monitoring.Providers;
using Xunit;

namespace Panelith.Monitoring;

public sealed class LibreHardwareMonitorSensorSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FacadeIsUnavailableOnNonWindowsWithoutThrowing()
    {
        var source = new LibreHardwareMonitorFacade();
        Assert.Equal("lhm", source.Id);
        Assert.True(source.RequiresElevation);

        if (!OperatingSystem.IsWindows())
        {
            Assert.False(source.IsAvailable);
            Assert.Empty(await source.ReadAsync(Ct));
        }

        await source.DisposeAsync();
    }
}
