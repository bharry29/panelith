using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using NetSparkleUpdater;
using NetSparkleUpdater.Configurations;
using NetSparkleUpdater.Enums;
using NetSparkleUpdater.SignatureVerifiers;
using Panelith.Engine;
using IAssemblyAccessor = NetSparkleUpdater.Interfaces.IAssemblyAccessor;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Panelith.App.Services;

/// <summary>
/// Finds, downloads and installs new versions through NetSparkle, the Windows
/// port of Sparkle. The feed and every installer are signed with Ed25519, and
/// nothing is installed unless both signatures check out against
/// <see cref="PublicKey"/>.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    /// <summary>The signed appcast published with every GitHub release.</summary>
    public const string AppcastUrl = "https://github.com/bharry29/panelith/releases/latest/download/appcast.xml";

    /// <summary>The key release builds sign the appcast and installers with. Rotating it strands older installs.</summary>
    private const string PublicKey = "8yKZfMbW4dFvM6oNzqkGC1Wqxs6Gax3somUOJp4CFO8=";

    /// <summary>Passed to the Inno Setup installer: no wizard, then start Panelith again.</summary>
    internal const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ILogger _logger;
    private readonly SparkleUpdater? _sparkle;
    private readonly DispatcherTimer _timer;
    private string? _downloadedInstaller;
    private bool _installing;

    public UpdateService(AppPaths paths, AppLog log, bool checkAutomatically)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);
        _logger = log.CreateLogger("Panelith.App.Updates");
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        IsSupported = IsInstalledCopy();
        CheckAutomatically = checkAutomatically;
        _timer = new DispatcherTimer(FirstCheckDelay, DispatcherPriority.Background, (_, _) => OnTimer());

        if (!IsSupported)
        {
            Status = "Updates are installed through the Panelith installer. This copy was not installed with it, so it does not update itself.";
            return;
        }

        var accessor = new VersionAccessor(CurrentVersion);
        _sparkle = new SparkleUpdater(AppcastUrl, new Ed25519Checker(SecurityMode.Strict, PublicKey))
        {
            UIFactory = null,
            UserInteractionMode = UserInteractionMode.DownloadNoInstall,
            Configuration = new JSONConfiguration(accessor, Path.Combine(paths.Root, "updates.json")),
            CustomInstallerArguments = InstallerArguments,
            RelaunchAfterUpdate = false,
            LogWriter = new LogAdapter(_logger),
            TmpDownloadFilePath = Path.Combine(Path.GetTempPath(), "Panelith", "updates"),
        };
        _sparkle.DownloadMadeProgress += (_, _, e) => Dispatcher.UIThread.Post(() => DownloadPercent = e.ProgressPercentage);
        _sparkle.DownloadFinished += (_, path) => Dispatcher.UIThread.Post(() => OnDownloaded(path));
        _sparkle.DownloadHadError += (_, _, ex) => Dispatcher.UIThread.Post(() => OnDownloadFailed(ex.Message));
        _sparkle.DownloadedFileIsCorrupt += (_, _) => Dispatcher.UIThread.Post(() => OnDownloadFailed("the download's signature did not match, so it was thrown away"));
        _sparkle.CloseApplicationAsync += () => QuitRequested?.Invoke() ?? Task.CompletedTask;
        Status = "Panelith checks for updates in the background.";
        if (checkAutomatically)
        {
            _timer.Start();
        }
    }

    /// <summary>Raised once the installer is about to run; the app must exit so its files can be replaced.</summary>
    public event Func<Task>? QuitRequested;

    /// <summary>Raised on the UI thread when a newer version is found.</summary>
    public event EventHandler? UpdateFound;

    /// <summary>The version that is running.</summary>
    public string CurrentVersion { get; }

    /// <summary>Whether this copy can update itself: only installed copies can.</summary>
    public bool IsSupported { get; }

    [ObservableProperty]
    public partial bool CheckAutomatically { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(AvailableVersion), nameof(ReleaseNotesUrl))]
    public partial AppCastItem? Available { get; private set; }

    [ObservableProperty]
    public partial int DownloadPercent { get; private set; }

    public bool IsIdle => !IsBusy;

    public bool HasUpdate => Available is not null;

    public string? AvailableVersion => Available?.Version;

    public string ReleaseNotesUrl => Available is { } item
        ? $"https://github.com/bharry29/panelith/releases/tag/v{item.Version}"
        : "https://github.com/bharry29/panelith/releases/latest";

    partial void OnCheckAutomaticallyChanged(bool value)
    {
        if (_sparkle is null)
        {
            return;
        }

        if (value)
        {
            _timer.Interval = FirstCheckDelay;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    /// <summary>Asks the feed whether a newer version exists.</summary>
    public async Task CheckAsync(bool userAsked)
    {
        if (_sparkle is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "Checking for updates…";
        try
        {
            var info = await _sparkle.CheckForUpdatesQuietly(ignoreSkippedVersions: true);
            var newest = info.Status == UpdateStatus.UpdateAvailable
                ? info.Updates.Where(u => u.IsWindowsUpdate).OrderByDescending(u => u.SemVerLikeVersion).FirstOrDefault()
                : null;
            Available = newest;
            Status = info.Status switch
            {
                UpdateStatus.UpdateAvailable when newest is not null => $"Panelith {newest.Version} is available. You have {CurrentVersion}.",
                UpdateStatus.CouldNotDetermine => "The update server could not be reached, or its answer was not signed correctly. Panelith will try again later.",
                _ => $"Panelith {CurrentVersion} is the latest version. Last checked {DateTime.Now:t}.",
            };
            if (newest is not null)
            {
                _logger.LogInformation("Update available: {Version}", newest.Version);
                if (!userAsked)
                {
                    UpdateFound?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception ex)
        {
            // A failed check must never take the app down; the next one may work.
            _logger.LogWarning("Could not check for updates: {Message}", ex.Message);
            Status = $"Could not check for updates: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the available version, checks its signature and runs its installer.</summary>
    public async Task InstallAsync()
    {
        if (_sparkle is null || Available is not { } item || IsBusy)
        {
            return;
        }

        if (_downloadedInstaller is { } ready && File.Exists(ready))
        {
            await RunInstallerAsync(item, ready);
            return;
        }

        IsBusy = true;
        DownloadPercent = 0;
        Status = $"Downloading Panelith {item.Version}…";
        try
        {
            await _sparkle.InitAndBeginDownload(item);
        }
        catch (Exception ex)
        {
            OnDownloadFailed(ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        if (!_installing)
        {
            // While installing, NetSparkle is the caller that asked the app to quit.
            _sparkle?.Dispose();
        }
    }

    private void OnTimer()
    {
        _timer.Interval = CheckInterval;
        _ = CheckAsync(userAsked: false);
    }

    private void OnDownloaded(string path)
    {
        _downloadedInstaller = path;
        if (Available is { } item)
        {
            _ = RunInstallerAsync(item, path);
        }
    }

    private async Task RunInstallerAsync(AppCastItem item, string path)
    {
        IsBusy = true;
        _installing = true;
        Status = $"Installing Panelith {item.Version}. Panelith closes and opens again when it is done.";
        _logger.LogInformation("Installing update {Version} from {Path}", item.Version, path);
        try
        {
            await _sparkle!.InstallUpdate(item, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("The update could not be installed: {Message}", ex.Message);
            Status = $"The update could not be installed: {ex.Message}";
            _installing = false;
            IsBusy = false;
        }
    }

    private void OnDownloadFailed(string message)
    {
        _logger.LogWarning("The update could not be downloaded: {Message}", message);
        _downloadedInstaller = null;
        Status = $"The update could not be downloaded: {message}.";
        IsBusy = false;
    }

    private static bool IsInstalledCopy() =>
        OperatingSystem.IsWindows()
        && Path.GetDirectoryName(Environment.ProcessPath) is { } folder
        && File.Exists(Path.Combine(folder, "unins000.exe"));

    /// <summary>Tells NetSparkle which version is installed, without it reading the executable's resources.</summary>
    private sealed class VersionAccessor(string version) : IAssemblyAccessor
    {
        public string AssemblyCompany => "Bharath Vadlamannati";

        public string AssemblyCopyright => "Copyright (c) 2026 Bharath Vadlamannati";

        public string AssemblyDescription => "Panelith";

        public string AssemblyTitle => "Panelith";

        public string AssemblyProduct => "Panelith";

        public string AssemblyVersion => version;
    }

    private sealed class LogAdapter(ILogger logger) : NetSparkleUpdater.Interfaces.ILogger
    {
        public void PrintMessage(string message, params object[]? arguments)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("{Message}", arguments is { Length: > 0 } ? string.Format(System.Globalization.CultureInfo.InvariantCulture, message, arguments) : message);
            }
        }
    }
}
