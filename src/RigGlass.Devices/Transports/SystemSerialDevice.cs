using System.Diagnostics;
using System.IO.Ports;

namespace RigGlass.Devices.Transports;

/// <summary>An open <see cref="SerialPort"/>.</summary>
internal sealed class SystemSerialDevice : ISerialDevice
{
    private SerialPort? _port;

    internal SystemSerialDevice(SerialPort port)
    {
        _port = port;
        PortName = port.PortName;
    }

    public string PortName { get; }

    public bool IsOpen => _port is { IsOpen: true };

    public void Write(ReadOnlySpan<byte> data)
    {
        var port = _port ?? throw Closed();

        try
        {
            port.BaseStream.Write(data);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Dispose();
            throw new PanelConnectionException(
                $"Failed to write to {PortName}: {ex.Message}",
                PanelFailureReason.Disconnected,
                ex);
        }
        catch (TimeoutException ex)
        {
            throw new PanelConnectionException(
                $"Writing to {PortName} timed out.",
                PanelFailureReason.Timeout,
                ex);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMilliseconds = 2000)
    {
        var port = _port ?? throw Closed();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // A serial read returns whatever has arrived, which for a framed panel
        // response is usually less than the whole frame. Looping until the
        // buffer fills or the budget runs out is what turns that into the
        // "read exactly this many bytes" contract the protocols are written
        // against.
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * (timeoutMilliseconds / 1000.0));
        var total = 0;

        while (total < buffer.Length)
        {
            var remaining = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
            if (remaining <= 0)
            {
                break;
            }

            try
            {
                port.ReadTimeout = remaining;
                var read = port.BaseStream.Read(buffer[total..]);
                if (read <= 0)
                {
                    break;
                }

                total += read;
            }
            catch (TimeoutException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Dispose();
                throw new PanelConnectionException(
                    $"Failed to read from {PortName}: {ex.Message}",
                    PanelFailureReason.Disconnected,
                    ex);
            }
        }

        return total;
    }

    public void DiscardInput()
    {
        try
        {
            _port?.DiscardInBuffer();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // Nothing to discard on a port that is already gone.
        }
    }

    public void Flush()
    {
        try
        {
            _port?.BaseStream.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // A flush failure is always reported again by the next write.
        }
    }

    public void Dispose()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is null)
        {
            return;
        }

        try
        {
            if (port.IsOpen)
            {
                port.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            // Closing a port whose device has been unplugged throws on some
            // platforms; the handle is released by Dispose regardless.
        }
        finally
        {
            port.Dispose();
        }
    }

    private PanelConnectionException Closed() => new(
        $"The connection to {PortName} is closed.",
        PanelFailureReason.Disconnected);
}
