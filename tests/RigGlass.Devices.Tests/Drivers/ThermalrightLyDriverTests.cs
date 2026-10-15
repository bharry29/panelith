using RigGlass.Devices.Drivers;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Protocols.Thermalright;
using RigGlass.Devices.Tests.Fakes;
using RigGlass.Devices.Transports;
using RigGlass.Imaging;

namespace RigGlass.Devices.Tests.Drivers;

public sealed class ThermalrightLyDriverTests
{
    private const int TrofeoVendor = 0x0416;
    private const int TrofeoProduct = 0x5408;

    [Fact]
    public async Task FindsTheTrofeoVisionOnTheBus()
    {
        var bus = new FakeUsbDeviceAccess().With(TrofeoVendor, TrofeoProduct, "TRV-0001");
        var driver = new ThermalrightLyDriver(ContextWith(bus));

        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        Assert.Equal("thermalright.trofeo-vision", panel.Descriptor.ProfileId);
        Assert.Equal(1920, panel.Descriptor.Width);
        Assert.Equal(480, panel.Descriptor.Height);
        Assert.Equal(ThermalrightLyDriver.DriverId, panel.DriverId);
        Assert.Equal("usb:0416:5408:sn=TRV-0001", panel.StableKey);
    }

    [Fact]
    public async Task IgnoresDevicesOtherDriversOwn()
    {
        // A Thermalright HID panel is on the bus, but it belongs to the HID
        // driver, so the LY driver must leave it alone rather than trying to
        // talk the wrong protocol to it.
        var bus = new FakeUsbDeviceAccess().With(TrofeoVendor, 0x5302);
        var driver = new ThermalrightLyDriver(ContextWith(bus));

        Assert.Empty(await driver.DiscoverAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportsNothingWhenTheBulkBackendIsUnavailable()
    {
        var bus = new FakeUsbDeviceAccess().With(TrofeoVendor, TrofeoProduct);
        bus.IsAvailable = false;

        var driver = new ThermalrightLyDriver(ContextWith(bus));

        Assert.Empty(await driver.DiscoverAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendsTheHandshakeAndKeepsTheProfileGeometryWhenThePanelAgrees()
    {
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 1));

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        await using var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default,
            TestContext.Current.CancellationToken);

        var device = Assert.Single(bus.OpenedBulkDevices);
        Assert.Equal(LyProtocol.BuildHandshake(), device.Writes[0]);
        Assert.Equal((1920, 480), connection.FrameSize);
    }

    [Fact]
    public async Task TrustsThePanelOverTheProfileWhenItReportsADifferentSize()
    {
        // The 9.16" and the 11.3" share one product id. A handshake whose model
        // byte is 0x05 is the 11.3" and must be driven at 1920x400 even though
        // the profile's default is the 9.16" at 1920x480.
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 5));

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        Assert.Equal((1920, 480), (panel.Descriptor.Width, panel.Descriptor.Height));

        await using var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.Equal((1920, 400), connection.FrameSize);
    }

    [Fact]
    public async Task RefusesToDriveAPanelThatAnswersWithAnotherProtocol()
    {
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, new byte[64]);

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<PanelConnectionException>(
            async () => await driver.OpenAsync(panel, PanelOpenOptions.Default, TestContext.Current.CancellationToken));

        Assert.Equal(PanelFailureReason.ProtocolError, error.Reason);

        // The handle must be released on a failed open, or the panel stays
        // locked until the process exits.
        Assert.Equal(1, Assert.Single(bus.OpenedBulkDevices).DisposeCount);
    }

    [Fact]
    public async Task ReportsASilentPanelAsNotReadyRatherThanBroken()
    {
        var bus = new FakeUsbDeviceAccess().With(TrofeoVendor, TrofeoProduct);
        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        var error = await Assert.ThrowsAsync<PanelConnectionException>(
            async () => await driver.OpenAsync(panel, PanelOpenOptions.Default, TestContext.Current.CancellationToken));

        Assert.Equal(PanelFailureReason.NotReady, error.Reason);
        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task SendsAFrameAsChunkedJpegInWholeBulkWrites()
    {
        var codec = new StubImageCodec { NextSize = 5000 };
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 1));

        var driver = new ThermalrightLyDriver(ContextWith(bus, codec));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        await using var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default,
            TestContext.Current.CancellationToken);

        using var frame = FrameBuffer.RentCleared(1920, 480);
        await connection.SendFrameAsync(frame, TestContext.Current.CancellationToken);

        var device = Assert.Single(bus.OpenedBulkDevices);
        var frameWrites = device.Writes.Skip(1).ToList();

        Assert.Equal(LyProtocol.TransferSizeFor(5000, TrofeoProduct), frameWrites.Sum(w => w.Length));

        // Every write but the last fills a whole bulk transfer, and the last
        // lands on a 2 KiB chunk-group boundary: that is what the chunk-count
        // padding exists to guarantee, and the firmware stalls without it.
        Assert.All(frameWrites.SkipLast(1), write => Assert.Equal(LyProtocol.BulkWriteSize, write.Length));
        Assert.Equal(0, frameWrites[^1].Length % 2048);

        // The payload must survive the chunking: reassembling the data portion
        // of every chunk has to give back exactly what the codec produced.
        var expected = codec.EncodeJpeg(frame);
        Assert.Equal(expected, Reassemble([.. frameWrites.SelectMany(w => w)], expected.Length));
    }

    [Fact]
    public async Task RejectsAFrameOfTheWrongSize()
    {
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 1));

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        await using var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default,
            TestContext.Current.CancellationToken);

        using var frame = FrameBuffer.RentCleared(320, 240);

        var error = await Assert.ThrowsAsync<PanelConnectionException>(
            async () => await connection.SendFrameAsync(frame, TestContext.Current.CancellationToken));

        Assert.Equal(PanelFailureReason.ProtocolError, error.Reason);
    }

    [Fact]
    public async Task RendersAtSwappedDimensionsWhenRotatedAQuarterTurn()
    {
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 1));

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        await using var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default with { Orientation = PanelOrientation.Landscape },
            TestContext.Current.CancellationToken);

        Assert.Equal((480, 1920), connection.FrameSize);

        using var frame = FrameBuffer.RentCleared(480, 1920);
        await connection.SendFrameAsync(frame, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ClosesTheHandleWhenTheConnectionIsDisposed()
    {
        var bus = new FakeUsbDeviceAccess()
            .With(TrofeoVendor, TrofeoProduct)
            .Replying(TrofeoVendor, TrofeoProduct, HandshakeReply(rawModel: 1));

        var driver = new ThermalrightLyDriver(ContextWith(bus));
        var panel = Assert.Single(await driver.DiscoverAsync(TestContext.Current.CancellationToken));

        var connection = await driver.OpenAsync(
            panel,
            PanelOpenOptions.Default,
            TestContext.Current.CancellationToken);

        var device = Assert.Single(bus.OpenedBulkDevices);
        Assert.True(device.IsOpen);

        await connection.DisposeAsync();

        Assert.False(device.IsOpen);
        Assert.False(connection.IsConnected);
    }

    /// <summary>
    /// A handshake reply the driver will accept.
    /// </summary>
    /// <param name="rawModel">
    /// The model byte at offset 20. The firmware adds 64 to it, so <c>4</c> is
    /// the 6.86" panel and <c>5</c> the 11.3" one.
    /// </param>
    /// <param name="subModel">The sub-model byte at offset 22.</param>
    private static byte[] HandshakeReply(int rawModel, int subModel = 0)
    {
        var reply = new byte[LyProtocol.ResponseSize];
        reply[0] = 3;
        reply[1] = 0xFF;
        reply[8] = 1;
        reply[20] = (byte)rawModel;
        reply[22] = (byte)subModel;
        return reply;
    }

    /// <summary>Strips the chunk headers back off a transfer.</summary>
    private static byte[] Reassemble(byte[] transfer, int payloadSize)
    {
        var payload = new byte[payloadSize];
        var written = 0;

        for (var offset = 0; offset + LyProtocol.ChunkSize <= transfer.Length && written < payloadSize; offset += LyProtocol.ChunkSize)
        {
            var length = Math.Min(LyProtocol.ChunkDataSize, payloadSize - written);
            transfer.AsSpan(offset + LyProtocol.ChunkHeaderSize, length).CopyTo(payload.AsSpan(written));
            written += length;
        }

        return payload;
    }

    private static PanelDriverContext ContextWith(IUsbDeviceAccess bus, StubImageCodec? codec = null) => new(
        PanelProfileRegistry.Load(),
        codec ?? new StubImageCodec(),
        bus,
        new FakeUsbDeviceAccess("fake-hid"),
        new FakeSerialPortEnumerator());
}
