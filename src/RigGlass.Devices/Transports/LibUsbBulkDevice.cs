using Microsoft.Extensions.Logging;
using RigGlass.Devices.Transports.Native;

namespace RigGlass.Devices.Transports;

/// <summary>An open libusb handle with one claimed bulk interface.</summary>
internal sealed class LibUsbBulkDevice : IUsbBulkDevice
{
    private readonly ILogger? _logger;
    private nint _context;
    private nint _handle;

    internal LibUsbBulkDevice(
        nint context,
        nint handle,
        UsbBulkEndpoints endpoints,
        UsbDeviceInfo info,
        ILogger? logger)
    {
        _context = context;
        _handle = handle;
        _logger = logger;
        Endpoints = endpoints;
        Info = info;
    }

    public UsbDeviceInfo Info { get; }

    public UsbBulkEndpoints Endpoints { get; }

    public bool IsOpen => _handle != nint.Zero;

    public unsafe int Write(ReadOnlySpan<byte> data, int timeoutMilliseconds = 5000)
    {
        ThrowIfClosed();

        if (data.IsEmpty)
        {
            return 0;
        }

        fixed (byte* pointer = data)
        {
            var result = (LibUsbError)LibUsb.BulkTransfer(
                _handle,
                Endpoints.EndpointOut,
                pointer,
                data.Length,
                out var transferred,
                (uint)timeoutMilliseconds);

            if (result == LibUsbError.Success)
            {
                return transferred;
            }

            // A timeout that moved some bytes is reported with the partial
            // count, which the chunked panel protocols can resume from; a
            // timeout that moved nothing is a genuine stall.
            if (result == LibUsbError.Timeout && transferred > 0)
            {
                return transferred;
            }

            if (result == LibUsbError.Pipe)
            {
                ClearHalt(Endpoints.EndpointOut);
            }

            throw Failure("write to", result);
        }
    }

    public unsafe int Read(Span<byte> buffer, int timeoutMilliseconds = 5000)
    {
        ThrowIfClosed();

        if (Endpoints.EndpointIn is not { } endpoint)
        {
            throw new PanelConnectionException(
                $"{Info.Describe()} has no bulk IN endpoint to read from.",
                PanelFailureReason.Unsupported);
        }

        if (buffer.IsEmpty)
        {
            return 0;
        }

        fixed (byte* pointer = buffer)
        {
            var result = (LibUsbError)LibUsb.BulkTransfer(
                _handle,
                endpoint,
                pointer,
                buffer.Length,
                out var transferred,
                (uint)timeoutMilliseconds);

            if (result is LibUsbError.Success)
            {
                return transferred;
            }

            if (result == LibUsbError.Timeout)
            {
                return transferred;
            }

            if (result == LibUsbError.Pipe)
            {
                ClearHalt(endpoint);
            }

            throw Failure("read from", result);
        }
    }

    public void ClearHalt(byte endpoint)
    {
        if (!IsOpen)
        {
            return;
        }

        var result = (LibUsbError)LibUsb.ClearHalt(_handle, endpoint);
        if (result != LibUsbError.Success)
        {
            _logger?.LogDebug(
                "Could not clear halt on endpoint {Endpoint:x2} of {Device}: {Error}",
                endpoint,
                Info.Describe(),
                LibUsb.Describe(result));
        }
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            _ = LibUsb.ReleaseInterface(handle, Endpoints.InterfaceNumber);
            LibUsb.Close(handle);
        }

        var context = Interlocked.Exchange(ref _context, nint.Zero);
        if (context != nint.Zero)
        {
            LibUsb.Exit(context);
        }
    }

    private void ThrowIfClosed()
    {
        if (!IsOpen)
        {
            throw new PanelConnectionException(
                $"The connection to {Info.Describe()} is closed.",
                PanelFailureReason.Disconnected);
        }
    }

    private PanelConnectionException Failure(string operation, LibUsbError error)
    {
        var reason = LibUsb.ToFailureReason(error);

        // A device that vanishes mid-transfer must not be retried against a
        // stale handle: close now so the supervisor reopens from scratch.
        if (reason == PanelFailureReason.Disconnected)
        {
            Dispose();
        }

        return new PanelConnectionException(
            $"Failed to {operation} {Info.Describe()}: {LibUsb.Describe(error)}",
            reason);
    }
}
