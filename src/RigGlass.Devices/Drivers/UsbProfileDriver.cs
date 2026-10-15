using Microsoft.Extensions.Logging;
using RigGlass.Devices.Profiles;
using RigGlass.Devices.Transports;

namespace RigGlass.Devices.Drivers;

/// <summary>
/// Shared discovery for drivers whose panels are found by USB vendor and
/// product id.
/// </summary>
/// <remarks>
/// <para>
/// Discovery is deliberately cheap and non-destructive: enumeration is filtered
/// to the ids the catalogue knows about, and nothing is opened or claimed. A
/// panel currently being driven by the vendor's own tool therefore shows up in
/// the device list and keeps working until the user deliberately switches it
/// over, which is the behaviour people expect when they are evaluating a
/// replacement for software they already use.
/// </para>
/// <para>
/// Resolution comes from the profile at this stage, so the reported confidence
/// is <see cref="PanelMatchConfidence.IdMatch"/>. Drivers that can ask the
/// hardware for its real geometry do so when the panel is opened and correct
/// the descriptor then, because a handshake needs a claimed interface and
/// discovery must not take one.
/// </para>
/// </remarks>
public abstract class UsbProfileDriver : IPanelDriver
{
    /// <summary>Creates the driver.</summary>
    /// <param name="context">Shared services.</param>
    protected UsbProfileDriver(PanelDriverContext context)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Logger = context.LoggerFactory.CreateLogger(GetType());
    }

    /// <inheritdoc />
    public abstract string Id { get; }

    /// <inheritdoc />
    public abstract string DisplayName { get; }

    /// <summary>Shared services.</summary>
    protected PanelDriverContext Context { get; }

    /// <summary>Log sink for this driver.</summary>
    protected ILogger Logger { get; }

    /// <summary>Which USB backend this driver's panels are opened through.</summary>
    protected abstract IUsbDeviceAccess Access { get; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<DetectedPanel>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profiles = Context.Profiles.ForDriver(Id);
        if (profiles.Count == 0 || !Access.IsAvailable)
        {
            return ValueTask.FromResult<IReadOnlyList<DetectedPanel>>([]);
        }

        var wanted = new HashSet<(int VendorId, int ProductId)>();
        foreach (var profile in profiles)
        {
            foreach (var match in profile.Usb)
            {
                try
                {
                    wanted.Add((match.VendorId, match.ProductId));
                }
                catch (PanelProfileException)
                {
                    // Reported when the catalogue was loaded; skip the bad entry.
                }
            }
        }

        var detected = new List<DetectedPanel>();
        foreach (var device in Access.Enumerate(wanted))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var profile = SelectProfile(device);
            if (profile is null)
            {
                continue;
            }

            detected.Add(new DetectedPanel(
                profile.ToDescriptor(),
                ToLocation(device, profile),
                Id,
                profile.IsUserDefined ? PanelMatchConfidence.UserDefined : PanelMatchConfidence.IdMatch));
        }

        return ValueTask.FromResult<IReadOnlyList<DetectedPanel>>(detected);
    }

    /// <inheritdoc />
    public abstract ValueTask<IPanelConnection> OpenAsync(
        DetectedPanel panel,
        PanelOpenOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Picks the best profile for a device, or none if this driver does not own it.</summary>
    protected PanelProfile? SelectProfile(UsbDeviceInfo device)
    {
        foreach (var candidate in Context.Profiles.FindByUsb(device.VendorId, device.ProductId, device.SerialNumber))
        {
            if (string.Equals(candidate.Driver, Id, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Finds the profile a previously discovered panel was matched against.</summary>
    /// <exception cref="PanelConnectionException">The catalogue no longer describes the panel.</exception>
    protected PanelProfile ProfileFor(DetectedPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        return Context.Profiles.Find(panel.Descriptor.ProfileId)
            ?? throw new PanelConnectionException(
                $"No profile named \"{panel.Descriptor.ProfileId}\" is loaded any more.",
                PanelFailureReason.Unsupported);
    }

    /// <summary>Rebuilds the USB identity needed to reopen a discovered panel.</summary>
    protected static UsbDeviceInfo ToDeviceInfo(DetectedPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        var location = panel.Location;
        return new UsbDeviceInfo(
            location.VendorId,
            location.ProductId,
            location.BusNumber,
            location.DeviceAddress,
            location.SerialNumber,
            Manufacturer: null,
            panel.Descriptor.Model,
            location.DevicePath);
    }

    /// <summary>Waits out a profile's startup delay, if it declares one.</summary>
    protected async ValueTask WaitForStartupAsync(PanelProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (profile.StartupDelaySeconds <= 0)
        {
            return;
        }

        Logger.LogDebug(
            "Waiting {Seconds:0.##}s for {Panel} to finish starting up.",
            profile.StartupDelaySeconds,
            profile.FullName);

        await Task.Delay(
            TimeSpan.FromSeconds(profile.StartupDelaySeconds),
            Context.TimeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    private PanelLocation ToLocation(UsbDeviceInfo device, PanelProfile profile) => PanelLocation.Usb(
        profile.Transport,
        device.VendorId,
        device.ProductId,
        device.BusNumber,
        device.DeviceAddress,
        device.DevicePath,
        device.SerialNumber);
}
