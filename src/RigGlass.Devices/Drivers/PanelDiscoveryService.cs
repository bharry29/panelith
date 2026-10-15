using Microsoft.Extensions.Logging;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// Runs every driver's discovery and presents one de-duplicated device list.
/// </summary>
/// <remarks>
/// <para>
/// Two drivers can legitimately claim the same physical device: a panel that
/// presents both a HID interface and a vendor bulk interface will be seen by
/// both backends. De-duplicating on
/// <see cref="PanelLocation.StableKey"/> and keeping the most confident match
/// is what stops the same screen appearing twice in the UI.
/// </para>
/// <para>
/// A driver that throws is logged and skipped rather than being allowed to
/// empty the list. Discovery runs on a timer for hotplug detection, so one
/// backend failing - a missing libusb, a permissions problem on one device -
/// must not take the user's working panels off the list.
/// </para>
/// </remarks>
public sealed class PanelDiscoveryService
{
    private readonly IReadOnlyList<IPanelDriver> _drivers;
    private readonly ILogger _logger;

    /// <summary>Creates the service.</summary>
    /// <param name="drivers">The drivers to poll.</param>
    /// <param name="logger">Log sink.</param>
    public PanelDiscoveryService(IEnumerable<IPanelDriver> drivers, ILogger<PanelDiscoveryService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(drivers);

        _drivers = [.. drivers];
        _logger = logger as ILogger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>The drivers being polled.</summary>
    public IReadOnlyList<IPanelDriver> Drivers => _drivers;

    /// <summary>Builds the default driver set for a context.</summary>
    public static PanelDiscoveryService CreateDefault(
        PanelDriverContext context,
        VirtualPanelOptions? virtualOptions = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        return new PanelDiscoveryService(
            [
                new ThermalrightLyDriver(context),
                new ThermalrightBulkDriver(context),
                new ThermalrightHidDriver(context),
                new ThermalrightScsiDriver(context),
                new TuringSerialDriver(context),
                new RecognizedOnlyDriver(context),
                new VirtualPanelDriver(context, virtualOptions),
            ],
            context.LoggerFactory.CreateLogger<PanelDiscoveryService>());
    }

    /// <summary>Finds every panel attached right now.</summary>
    public async ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var byKey = new Dictionary<string, DetectedPanel>(StringComparer.Ordinal);

        foreach (var driver in _drivers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<DetectedPanel> found;
            try
            {
                found = await driver.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The {Driver} driver failed during discovery and was skipped.", driver.Id);
                continue;
            }

            foreach (var panel in found)
            {
                var key = panel.StableKey;
                if (!byKey.TryGetValue(key, out var existing) || IsBetter(panel, existing))
                {
                    byKey[key] = panel;
                }
            }
        }

        return [.. byKey.Values
            .OrderBy(p => p.Descriptor.Vendor, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Descriptor.Model, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.StableKey, StringComparer.Ordinal)];
    }

    /// <summary>Finds the driver that reported a panel.</summary>
    public IPanelDriver? DriverFor(DetectedPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        foreach (var driver in _drivers)
        {
            if (string.Equals(driver.Id, panel.DriverId, StringComparison.OrdinalIgnoreCase))
            {
                return driver;
            }
        }

        return null;
    }

    /// <summary>Opens a discovered panel using the driver that found it.</summary>
    /// <exception cref="PanelConnectionException">No driver can open the panel.</exception>
    public ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var driver = DriverFor(panel)
            ?? throw new PanelConnectionException(
                $"The \"{panel.DriverId}\" driver is not registered, so this panel cannot be opened.",
                PanelFailureReason.Unsupported);

        return driver.OpenAsync(panel, options, cancellationToken);
    }

    /// <summary>
    /// Whether one match should displace another for the same physical device.
    /// </summary>
    /// <remarks>
    /// A user-defined profile always wins, because the user knows what they
    /// plugged in. Otherwise a driver that can actually drive the panel beats
    /// one that merely recognises it, and a confirmed geometry beats a guess.
    /// </remarks>
    private static bool IsBetter(DetectedPanel candidate, DetectedPanel existing)
    {
        if (candidate.Confidence != existing.Confidence)
        {
            return Rank(candidate.Confidence) > Rank(existing.Confidence);
        }

        var candidateDrives = candidate.DriverId != RecognizedOnlyDriver.DriverId;
        var existingDrives = existing.DriverId != RecognizedOnlyDriver.DriverId;
        return candidateDrives && !existingDrives;
    }

    private static int Rank(PanelMatchConfidence confidence) => confidence switch
    {
        PanelMatchConfidence.UserDefined => 3,
        PanelMatchConfidence.Confirmed => 2,
        _ => 1,
    };
}
