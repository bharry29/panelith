using Panelith.Devices.Transports;

namespace Panelith.Devices.Tests.Fakes;

/// <summary>A scripted set of serial ports.</summary>
internal sealed class FakeSerialPortEnumerator : ISerialPortEnumerator
{
    private readonly List<SerialPortInfo> _ports = [];
    private readonly Dictionary<string, Queue<byte[]>> _replies = new(StringComparer.Ordinal);

    /// <summary>Every port opened, in order.</summary>
    internal List<FakeSerialDevice> Opened { get; } = [];

    internal FakeSerialPortEnumerator With(SerialPortInfo port)
    {
        _ports.Add(port);
        return this;
    }

    internal FakeSerialPortEnumerator With(string portName, int vendorId, int productId, string? serialNumber = null) =>
        With(new SerialPortInfo(portName, vendorId, productId, serialNumber));

    internal FakeSerialPortEnumerator Replying(string portName, params byte[][] replies)
    {
        var queue = _replies.TryGetValue(portName, out var existing)
            ? existing
            : _replies[portName] = new Queue<byte[]>();

        foreach (var reply in replies)
        {
            queue.Enqueue(reply);
        }

        return this;
    }

    public IReadOnlyList<SerialPortInfo> Enumerate() => _ports;

    public ISerialDevice Open(string portName, int baudRate, int readTimeoutMilliseconds = 2000)
    {
        var replies = _replies.TryGetValue(portName, out var queue)
            ? new Queue<byte[]>(queue)
            : new Queue<byte[]>();

        var device = new FakeSerialDevice(portName, baudRate, replies);
        Opened.Add(device);
        return device;
    }
}

/// <summary>A serial port that records writes and replays scripted reads.</summary>
internal sealed class FakeSerialDevice : ISerialDevice
{
    private readonly Queue<byte[]> _replies;

    internal FakeSerialDevice(string portName, int baudRate, Queue<byte[]> replies)
    {
        PortName = portName;
        BaudRate = baudRate;
        _replies = replies;
    }

    public string PortName { get; }

    /// <summary>The baud rate the port was opened at.</summary>
    internal int BaudRate { get; }

    public bool IsOpen { get; private set; } = true;

    /// <summary>Every write, in order.</summary>
    internal List<byte[]> Writes { get; } = [];

    /// <summary>Everything written, concatenated.</summary>
    internal byte[] WrittenBytes => [.. Writes.SelectMany(w => w)];

    /// <summary>How many times the input buffer was discarded.</summary>
    internal int DiscardCount { get; private set; }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(!IsOpen, this);
        Writes.Add(data.ToArray());
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

    public void DiscardInput() => DiscardCount++;

    public void Flush()
    {
    }

    public void Dispose() => IsOpen = false;
}
