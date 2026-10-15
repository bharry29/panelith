using OmniPanel.Devices.Transports;

namespace OmniPanel.Devices.Tests.Fakes;

/// <summary>A scripted USB bus, so driver behaviour can be asserted without hardware.</summary>
internal sealed class FakeUsbDeviceAccess : IUsbDeviceAccess
{
    private readonly List<UsbDeviceInfo> _devices = [];
    private readonly Dictionary<string, Queue<byte[]>> _replies = new(StringComparer.Ordinal);

    internal FakeUsbDeviceAccess(string backendName = "fake") => BackendName = backendName;

    public bool IsAvailable { get; set; } = true;

    public string BackendName { get; }

    /// <summary>Every bulk device opened through this backend, in order.</summary>
    internal List<FakeBulkDevice> OpenedBulkDevices { get; } = [];

    /// <summary>Every HID device opened through this backend, in order.</summary>
    internal List<FakeHidDevice> OpenedHidDevices { get; } = [];

    /// <summary>What <see cref="OpenBulk"/> and <see cref="OpenHid"/> should throw, if anything.</summary>
    internal PanelConnectionException? OpenFailure { get; set; }

    internal UsbBulkEndpoints Endpoints { get; set; } = new(0, 0x01, 0x81, 512);

    internal FakeUsbDeviceAccess With(UsbDeviceInfo device)
    {
        _devices.Add(device);
        return this;
    }

    internal FakeUsbDeviceAccess With(int vendorId, int productId, string? serialNumber = null, int bus = 1, int address = 2) =>
        With(new UsbDeviceInfo(vendorId, productId, bus, address, serialNumber));

    /// <summary>Queues a reply that the next read on a device will return.</summary>
    internal FakeUsbDeviceAccess Replying(int vendorId, int productId, params byte[][] replies)
    {
        var queue = _replies.TryGetValue(Key(vendorId, productId), out var existing)
            ? existing
            : _replies[Key(vendorId, productId)] = new Queue<byte[]>();

        foreach (var reply in replies)
        {
            queue.Enqueue(reply);
        }

        return this;
    }

    public IReadOnlyList<UsbDeviceInfo> Enumerate(IReadOnlySet<(int VendorId, int ProductId)>? filter = null) =>
        !IsAvailable
            ? []
            : [.. _devices.Where(d => filter is null || filter.Contains((d.VendorId, d.ProductId)))];

    public IUsbBulkDevice OpenBulk(UsbDeviceInfo device, bool requireInputEndpoint = true)
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        var opened = new FakeBulkDevice(device, Endpoints, RepliesFor(device));
        OpenedBulkDevices.Add(opened);
        return opened;
    }

    public IUsbHidDevice OpenHid(UsbDeviceInfo device)
    {
        if (OpenFailure is not null)
        {
            throw OpenFailure;
        }

        var opened = new FakeHidDevice(device, RepliesFor(device));
        OpenedHidDevices.Add(opened);
        return opened;
    }

    private Queue<byte[]> RepliesFor(UsbDeviceInfo device) =>
        _replies.TryGetValue(Key(device.VendorId, device.ProductId), out var queue)
            ? new Queue<byte[]>(queue)
            : new Queue<byte[]>();

    private static string Key(int vendorId, int productId) => $"{vendorId:x4}:{productId:x4}";
}

/// <summary>A bulk endpoint pair that records writes and replays scripted reads.</summary>
internal sealed class FakeBulkDevice : IUsbBulkDevice
{
    private readonly Queue<byte[]> _replies;

    internal FakeBulkDevice(UsbDeviceInfo info, UsbBulkEndpoints endpoints, Queue<byte[]> replies)
    {
        Info = info;
        Endpoints = endpoints;
        _replies = replies;
    }

    public UsbDeviceInfo Info { get; }

    public UsbBulkEndpoints Endpoints { get; }

    public bool IsOpen { get; private set; } = true;

    /// <summary>Every write, in order.</summary>
    internal List<byte[]> Writes { get; } = [];

    /// <summary>Everything written, concatenated.</summary>
    internal byte[] WrittenBytes => [.. Writes.SelectMany(w => w)];

    /// <summary>How many times <see cref="Dispose"/> ran.</summary>
    internal int DisposeCount { get; private set; }

    public int Write(ReadOnlySpan<byte> data, int timeoutMilliseconds = 5000)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        Writes.Add(data.ToArray());
        return data.Length;
    }

    public int Read(Span<byte> buffer, int timeoutMilliseconds = 5000)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);

        if (_replies.Count == 0)
        {
            return 0;
        }

        var reply = _replies.Dequeue();
        var length = Math.Min(reply.Length, buffer.Length);
        reply.AsSpan(0, length).CopyTo(buffer);
        return length;
    }

    public void ClearHalt(byte endpoint)
    {
    }

    public void Dispose()
    {
        DisposeCount++;
        IsOpen = false;
    }
}

/// <summary>A HID device that records output reports and replays scripted input reports.</summary>
internal sealed class FakeHidDevice : IUsbHidDevice
{
    private readonly Queue<byte[]> _replies;

    internal FakeHidDevice(UsbDeviceInfo info, Queue<byte[]> replies)
    {
        Info = info;
        _replies = replies;
    }

    public UsbDeviceInfo Info { get; }

    public bool IsOpen { get; private set; } = true;

    public int OutputReportLength { get; set; } = 65;

    public int InputReportLength { get; set; } = 65;

    /// <summary>Every output report, in order.</summary>
    internal List<byte[]> Reports { get; } = [];

    public void Write(ReadOnlySpan<byte> report)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        Reports.Add(report.ToArray());
    }

    public int Read(Span<byte> buffer, int timeoutMilliseconds = 2000)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);

        if (_replies.Count == 0)
        {
            return 0;
        }

        var reply = _replies.Dequeue();
        var length = Math.Min(reply.Length, buffer.Length);
        reply.AsSpan(0, length).CopyTo(buffer);
        return length;
    }

    public void Dispose() => IsOpen = false;
}
