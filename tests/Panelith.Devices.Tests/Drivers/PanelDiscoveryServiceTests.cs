using Panelith.Devices.Drivers;
using Panelith.Devices.Profiles;
using Panelith.Devices.Tests.Fakes;
using Panelith.Devices.Transports;

namespace Panelith.Devices.Tests.Drivers;

public sealed class PanelDiscoveryServiceTests
{
    [Fact]
    public async Task AlwaysOffersTheVirtualPanelSoThemesCanBeBuiltWithNoHardware()
    {
        var service = PanelDiscoveryService.CreateDefault(EmptyContext());

        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Contains(panels, p => p.Descriptor.ProfileId == "panelith.virtual");
    }

    [Fact]
    public async Task HidesTheVirtualPanelWhenItIsSwitchedOff()
    {
        var service = PanelDiscoveryService.CreateDefault(
            EmptyContext(),
            new VirtualPanelOptions { Enabled = false });

        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(panels, panel => panel.Descriptor.ProfileId == "panelith.virtual");
    }

    [Fact]
    public async Task ReportsOnePanelWhenTwoDriversSeeTheSameDevice()
    {
        // 0416:5406 is matched by both the SCSI profile and - in this fixture -
        // a hand-written override, so the same physical screen is reported
        // twice before de-duplication.
        var bus = new FakeUsbDeviceAccess().With(0x0416, 0x5406, "TR-SCSI-1");
        var service = PanelDiscoveryService.CreateDefault(ContextWith(bus, bus));

        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Single(panels, p => p.StableKey == "usb:0416:5406:sn=TR-SCSI-1");
    }

    [Fact]
    public async Task KeepsGoingWhenOneDriverThrows()
    {
        var service = new PanelDiscoveryService([
            new ThrowingDriver(),
            new VirtualPanelDriver(EmptyContext()),
        ]);

        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(panels);
    }

    [Fact]
    public async Task ListsPanelsItCannotDriveSoTheUserSeesAnExplanationRatherThanAnEmptyList()
    {
        // The Turing 8.8" family is identifiable but not yet drivable. Hiding it
        // would leave its owners with no way to tell "unsupported" from "broken".
        var bus = new FakeUsbDeviceAccess().With(0x1CBE, 0x0088);
        var service = PanelDiscoveryService.CreateDefault(ContextWith(bus, bus));

        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);
        var panel = Assert.Single(panels, p => p.Descriptor.ProfileId == "turing.usb-8.8");

        var error = await Assert.ThrowsAsync<PanelConnectionException>(
            async () => await service.OpenAsync(panel, PanelOpenOptions.Default, TestContext.Current.CancellationToken));

        Assert.Equal(PanelFailureReason.Unsupported, error.Reason);
        Assert.Contains("cannot drive it yet", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsSerialPanelsThroughTheirBridgeSerialNumber()
    {
        var ports = new FakeSerialPortEnumerator()
            .With("/dev/ttyUSB0", 0x1A86, 0x5722, "USB35INCHIPSV2")
            .With("/dev/ttyUSB1", 0x1A86, 0x5722, "2017-2-25");

        var service = PanelDiscoveryService.CreateDefault(ContextWith(serialPorts: ports));
        var panels = await service.DiscoverAsync(TestContext.Current.CancellationToken);

        Assert.Single(panels, p => p.Descriptor.ProfileId == "turing.smart-screen-3.5");
        Assert.Single(panels, p => p.Descriptor.ProfileId == "xuanfang.3.5");
    }

    [Fact]
    public void EveryBuiltInProfileNamesADriverThatIsActuallyRegistered()
    {
        // A profile naming a driver nobody implements is invisible: it loads and
        // validates, then silently never matches anything.
        var registered = PanelDiscoveryService.CreateDefault(EmptyContext())
            .Drivers
            .Select(d => d.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orphans = PanelProfileRegistry.Load().Profiles
            .Where(p => !registered.Contains(p.Driver))
            .Select(p => $"{p.Id} -> {p.Driver}")
            .ToList();

        Assert.Empty(orphans);
    }

    [Fact]
    public async Task FailsHelpfullyWhenADriverIsNotRegistered()
    {
        var service = new PanelDiscoveryService([]);
        var panel = new DetectedPanel(
            new PanelDescriptor("x", "X", "Y", 10, 10, PanelTransportKind.Virtual, PanelPixelFormat.Rgb565BigEndian, PanelCapabilities.Minimal),
            PanelLocation.Virtual("x"),
            "nobody.owns.this");

        var error = await Assert.ThrowsAsync<PanelConnectionException>(
            async () => await service.OpenAsync(panel, PanelOpenOptions.Default, TestContext.Current.CancellationToken));

        Assert.Equal(PanelFailureReason.Unsupported, error.Reason);
    }

    private static PanelDriverContext EmptyContext() => ContextWith();

    private static PanelDriverContext ContextWith(
        IUsbDeviceAccess? bulk = null,
        IUsbDeviceAccess? hid = null,
        ISerialPortEnumerator? serialPorts = null) => new(
        PanelProfileRegistry.Load(),
        new StubImageCodec(),
        bulk ?? new FakeUsbDeviceAccess("fake-bulk"),
        hid ?? new FakeUsbDeviceAccess("fake-hid"),
        serialPorts ?? new FakeSerialPortEnumerator());

    private sealed class ThrowingDriver : IPanelDriver
    {
        public string Id => "throwing";

        public string DisplayName => "Throwing driver";

        public ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the bus fell over");

        public ValueTask<IPanelConnection> OpenAsync(
            DetectedPanel panel,
            PanelOpenOptions options,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the bus fell over");
    }
}
