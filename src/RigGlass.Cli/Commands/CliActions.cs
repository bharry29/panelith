using System.Globalization;
using RigGlass.Cli.Composition;
using RigGlass.Cli.Rendering;
using RigGlass.Cli.Support;
using RigGlass.Cli.Themes;
using RigGlass.Devices;
using RigGlass.Devices.Drivers;
using RigGlass.Engine;
using RigGlass.Engine.Configuration;
using RigGlass.Themes;

namespace RigGlass.Cli.Commands;

/// <summary>What <c>rigglass run</c> was asked to do.</summary>
public sealed record RunRequest(
    bool Verbose,
    bool DemoSensors,
    bool VirtualPanel,
    string? Theme,
    string? OutputDirectory,
    double? Seconds,
    double? FramesPerSecond,
    int? RotateDegrees,
    string? ForceProfile,
    int? VirtualWidth,
    int? VirtualHeight);

/// <summary>The work behind each command.</summary>
public static class CliActions
{
    /// <summary>Lists attached panels.</summary>
    public static async Task<int> DevicesAsync(
        bool verbose,
        bool demoSensors,
        bool virtualPanel,
        CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors,
            verbose,
            virtualPanel,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var panels = await runtime.Discovery.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (panels.Count == 0)
        {
            Console.WriteLine("No panel is attached.");
            Console.WriteLine();
            WriteTrofeoHint();
            return 0;
        }

        foreach (var panel in panels)
        {
            var profile = runtime.Profiles.Find(panel.Descriptor.ProfileId);
            Console.WriteLine(panel.Describe());
            Console.WriteLine($"  key      {panel.StableKey}");
            Console.WriteLine($"  driver   {panel.DriverId}");
            Console.WriteLine($"  profile  {panel.Descriptor.ProfileId} ({panel.Confidence}, {profile?.Support.ToString().ToLowerInvariant() ?? "unknown"})");
            Console.WriteLine($"  format   {panel.Descriptor.PixelFormat}");
            if (profile?.Notes is { Length: > 0 } notes)
            {
                Console.WriteLine($"  notes    {notes}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("Close Thermalright TRCC, or any other app using the panel, before 'rigglass run'.");
        return 0;
    }

    /// <summary>Drives attached panels until cancelled or the time limit elapses.</summary>
    public static async Task<int> RunAsync(RunRequest request, CancellationToken cancellationToken)
    {
        if (request.RotateDegrees is { } degrees && !Program.TryParseRotation(degrees, out _))
        {
            Console.Error.WriteLine("--rotate must be 0, 90, 180, or 270.");
            return 1;
        }

        if (request.FramesPerSecond is { } fps && fps is <= 0 or > 120)
        {
            Console.Error.WriteLine("--fps must be between 0 and 120.");
            return 1;
        }

        if (request.Seconds is { } seconds && seconds <= 0)
        {
            Console.Error.WriteLine("--seconds must be greater than 0.");
            return 1;
        }

        VirtualPanelOptions? virtualOptions = request.VirtualPanel
            ? new VirtualPanelOptions
            {
                Enabled = true,
                Width = request.VirtualWidth,
                Height = request.VirtualHeight,
                OutputDirectory = request.OutputDirectory,
            }
            : null;

        await using var runtime = await RigGlassRuntime.StartAsync(
            request.DemoSensors,
            request.Verbose,
            request.VirtualPanel,
            virtualOptions,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var problem in runtime.Store.Problems.Concat(runtime.Profiles.Problems))
        {
            Console.Error.WriteLine(problem);
        }

        var options = ApplyRunOverrides(runtime.Options, request);
        var problems = options.Validate();
        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                Console.Error.WriteLine(problem);
            }

            return 1;
        }

        if (request.Theme is { Length: > 0 } themeId)
        {
            try
            {
                _ = new ThemeLibrary(runtime.Paths).Load(themeId);
            }
            catch (Exception ex) when (ex is FileNotFoundException or ThemeFormatException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        if (request.Seconds is { } limit)
        {
            stop.CancelAfter(TimeSpan.FromSeconds(limit));
        }

        var host = new PanelHost(
            runtime.Discovery,
            new ThemePanelRendererFactory(new ThemeLibrary(runtime.Paths)),
            runtime.Bindings,
            options,
            runtime.Loggers);

        try
        {
            await host.StartAsync(stop.Token).ConfigureAwait(false);
            var panels = host.Panels;
            if (panels.Count == 0)
            {
                Console.WriteLine("No panel is attached yet. RigGlass will start drawing when one appears.");
                Console.WriteLine();
                WriteTrofeoHint();
            }
            else
            {
                Console.WriteLine($"Driving {panels.Count} panel{(panels.Count == 1 ? "" : "s")} with theme \"{options.Defaults.Theme}\".");
                foreach (var panel in panels)
                {
                    Console.WriteLine($"  {panel.Describe()}");
                    Console.WriteLine($"  key {panel.StableKey}");
                }
            }

            if (request.Seconds is null)
            {
                Console.WriteLine("Press Ctrl+C to stop.");
            }

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                foreach (var status in host.Statuses)
                {
                    Console.WriteLine(status.Describe());
                    if (status.Remedy is { Length: > 0 } remedy)
                    {
                        Console.WriteLine($"  {remedy}");
                    }
                }
            }

            return FailedPermanently(host) ? 1 : 0;
        }
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Renders a theme to one or more PNG files.</summary>
    public static async Task<int> PreviewAsync(
        bool verbose,
        bool demoSensors,
        string themeId,
        string output,
        int? width,
        int? height,
        bool trofeoSizes,
        CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors,
            verbose,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        Theme theme;
        try
        {
            theme = new ThemeLibrary(runtime.Paths).Load(themeId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ThemeFormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var diagnostics = ThemeValidator.Validate(theme);
        foreach (var diagnostic in diagnostics)
        {
            var writer = diagnostic.Severity == ThemeDiagnosticSeverity.Error ? Console.Error : Console.Out;
            writer.WriteLine(diagnostic.ToString());
        }

        if (ThemeValidator.HasErrors(diagnostics))
        {
            return 1;
        }

        for (var i = 0; i < 4; i++)
        {
            await runtime.Sensors.PollOnceAsync(cancellationToken).ConfigureAwait(false);
        }

        var sizes = new List<(int Width, int Height, string Path)>();
        var previewWidth = width is > 0 ? width.Value : theme.Canvas.Width;
        var previewHeight = height is > 0 ? height.Value : theme.Canvas.Height;
        sizes.Add((previewWidth, previewHeight, output));

        if (trofeoSizes)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(output));
            var stem = Path.GetFileNameWithoutExtension(output);
            var extension = Path.GetExtension(output);
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".png";
            }

            sizes.Add((1280, 480, Path.Combine(directory ?? ".", $"{stem}-1280x480{extension}")));
            sizes.Add((1920, 480, Path.Combine(directory ?? ".", $"{stem}-1920x480{extension}")));
        }

        foreach (var size in sizes.DistinctBy(s => (s.Width, s.Height, s.Path)))
        {
            var png = ThemePreview.RenderPng(theme, runtime.Bindings, runtime.Codec, size.Width, size.Height);
            var fullPath = Path.GetFullPath(size.Path);
            var folder = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            await File.WriteAllBytesAsync(fullPath, png, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Wrote {fullPath} ({size.Width}x{size.Height}, {png.Length.ToString("N0", CultureInfo.InvariantCulture)} bytes)");
        }

        return 0;
    }

    /// <summary>Checks the local machine and optionally installs udev rules.</summary>
    public static async Task<int> DoctorAsync(
        bool verbose,
        bool installUdev,
        bool printUdev,
        CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors: false,
            verbose,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var rules = UdevRules.Build(runtime.Profiles);
        if (printUdev)
        {
            Console.WriteLine(rules);
            return 0;
        }

        if (installUdev)
        {
            return InstallUdevRules(rules);
        }

        Console.WriteLine("RigGlass doctor");
        Console.WriteLine($"  config     {runtime.Paths.ConfigurationFile}");
        Console.WriteLine($"  themes     {runtime.Paths.ThemesDirectory}");
        Console.WriteLine($"  profiles   {runtime.Profiles.Profiles.Count} built in, plus any JSON in {runtime.Paths.ProfilesDirectory}");

        if (runtime.Store.Problems.Count == 0)
        {
            Console.WriteLine("  config     readable");
        }
        else
        {
            foreach (var problem in runtime.Store.Problems)
            {
                Console.WriteLine($"  config     {problem}");
            }
        }

        var libusb = UdevRules.TryLoadLibUsb(out var libusbDetail);
        Console.WriteLine(libusb
            ? $"  libusb     found ({libusbDetail})"
            : $"  libusb     {libusbDetail}");

        if (OperatingSystem.IsLinux())
        {
            Console.WriteLine(File.Exists(UdevRules.InstallPath)
                ? $"  udev       {UdevRules.InstallPath} is installed"
                : "  udev       not installed. Run: sudo rigglass doctor --install-udev-rules");
            Console.WriteLine("             Then unplug the panel and plug it back in.");
        }
        else if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("  windows    bulk panels (Trofeo 9.16\" / 11.3\", id 0416:5408) need a WinUSB driver.");
            Console.WriteLine("             The 6.86\" (0416:5302) uses HID and usually needs no extra driver.");
            Console.WriteLine("             Close Thermalright TRCC before RigGlass opens the panel.");
        }

        var themeLibrary = new ThemeLibrary(runtime.Paths);
        try
        {
            var theme = themeLibrary.Load(runtime.Options.Defaults.Theme);
            var diagnostics = ThemeValidator.Validate(theme);
            var errors = diagnostics.Count(d => d.Severity == ThemeDiagnosticSeverity.Error);
            Console.WriteLine(errors == 0
                ? $"  theme      \"{theme.Name}\" ({theme.Id}) is valid"
                : $"  theme      \"{theme.Name}\" has {errors} error(s). Run: rigglass theme validate {theme.Id}");
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"  theme      \"{runtime.Options.Defaults.Theme}\" was not found. Run: rigglass theme list");
        }

        Console.WriteLine();
        WriteTrofeoHint();
        return libusb ? 0 : 1;
    }

    /// <summary>Lists known panel models.</summary>
    public static async Task<int> ProfilesAsync(bool verbose, CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors: false,
            verbose,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var profile in runtime.Profiles.Profiles)
        {
            var usb = profile.Usb.Count == 0
                ? "no usb id"
                : string.Join(", ", profile.Usb.Select(match => $"{match.Vendor}:{match.Product}"));
            Console.WriteLine($"{profile.Id}");
            Console.WriteLine($"  {profile.FullName}  {profile.Width}x{profile.Height}  {profile.PixelFormat}  {profile.Driver}  {profile.Support.ToString().ToLowerInvariant()}");
            Console.WriteLine($"  usb {usb}");
            if (profile.Notes is { Length: > 0 } notes)
            {
                Console.WriteLine($"  {notes}");
            }

            Console.WriteLine();
        }

        if (runtime.Profiles.Problems.Count > 0)
        {
            Console.Error.WriteLine("Some profile files were skipped:");
            foreach (var problem in runtime.Profiles.Problems)
            {
                Console.Error.WriteLine(problem);
            }
        }

        return 0;
    }

    /// <summary>Lists theme documents.</summary>
    public static async Task<int> ThemeListAsync(bool verbose, CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors: false,
            verbose,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var library = new ThemeLibrary(runtime.Paths);
        var files = library.List();
        if (files.Count == 0)
        {
            Console.WriteLine($"No themes found in {runtime.Paths.ThemesDirectory} or next to the rigglass executable.");
            return 0;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!seen.Add(Path.GetFullPath(file)))
            {
                continue;
            }

            try
            {
                var theme = library.Load(file);
                Console.WriteLine($"{theme.Id,-16} {theme.Name}  {theme.Canvas.Width}x{theme.Canvas.Height}  {file}");
            }
            catch (ThemeFormatException ex)
            {
                Console.WriteLine($"{"?",-16} unreadable  {file}");
                Console.WriteLine($"  {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>Validates one theme, or the configured default.</summary>
    public static async Task<int> ThemeValidateAsync(
        bool verbose,
        string? themeId,
        CancellationToken cancellationToken)
    {
        await using var runtime = await RigGlassRuntime.StartAsync(
            demoSensors: true,
            verbose,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        themeId = string.IsNullOrWhiteSpace(themeId) ? runtime.Options.Defaults.Theme : themeId;
        Theme theme;
        try
        {
            theme = new ThemeLibrary(runtime.Paths).Load(themeId);
        }
        catch (Exception ex) when (ex is FileNotFoundException or ThemeFormatException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var known = new HashSet<string>(runtime.Sensors.Aliases.Keys, StringComparer.OrdinalIgnoreCase);
        var diagnostics = ThemeValidator.Validate(theme, known.Count == 0 ? null : known);
        foreach (var diagnostic in diagnostics)
        {
            Console.WriteLine(diagnostic.ToString());
        }

        if (diagnostics.Count == 0)
        {
            Console.WriteLine($"\"{theme.Name}\" is valid.");
        }

        return ThemeValidator.HasErrors(diagnostics) ? 1 : 0;
    }

    private static RigGlassOptions ApplyRunOverrides(RigGlassOptions options, RunRequest request)
    {
        var defaults = options.Defaults;
        if (request.Theme is { Length: > 0 } theme)
        {
            defaults = defaults with { Theme = theme };
        }

        if (request.FramesPerSecond is { } fps)
        {
            defaults = defaults with { FramesPerSecond = fps };
        }

        if (request.RotateDegrees is { } degrees && Program.TryParseRotation(degrees, out var orientation))
        {
            defaults = defaults with { Orientation = orientation };
        }

        if (request.ForceProfile is { Length: > 0 } profile)
        {
            defaults = defaults with { ForceProfile = profile };
        }

        return options with { Defaults = defaults };
    }

    private static bool FailedPermanently(PanelHost host)
    {
        var statuses = host.Statuses;
        return statuses.Count > 0
            && statuses.All(status => status.State is PanelRuntimeState.Failed or PanelRuntimeState.Disabled);
    }

    private static int InstallUdevRules(string rules)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("udev rules are only used on Linux.");
            Console.Error.WriteLine("On Windows, the Trofeo Vision 6.86\" (0416:5302) uses HID.");
            Console.Error.WriteLine("The 9.16\" and 11.3\" (0416:5408) need a WinUSB driver, which Thermalright's installer usually sets up.");
            return 1;
        }

        if (!Environment.IsPrivilegedProcess)
        {
            Console.Error.WriteLine("Installing udev rules needs root.");
            Console.Error.WriteLine("Run: sudo rigglass doctor --install-udev-rules");
            Console.Error.WriteLine("To review them first: rigglass doctor --print-udev-rules");
            return 1;
        }

        Directory.CreateDirectory("/etc/udev/rules.d");
        File.WriteAllText(UdevRules.InstallPath, rules);
        Console.WriteLine($"Wrote {UdevRules.InstallPath}");

        if (!ReloadUdev())
        {
            Console.Error.WriteLine("The rules file was written, but udevadm could not be run.");
            Console.Error.WriteLine("Reload it yourself: sudo udevadm control --reload-rules && sudo udevadm trigger");
            return 1;
        }

        Console.WriteLine("Reloaded udev. Unplug the panel and plug it back in, then run: rigglass devices");
        return 0;
    }

    private static bool ReloadUdev()
    {
        try
        {
            using var reload = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "udevadm",
                ArgumentList = { "control", "--reload-rules" },
                UseShellExecute = false,
            });
            if (reload is null || !reload.WaitForExit(5000) || reload.ExitCode != 0)
            {
                return false;
            }

            using var trigger = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "udevadm",
                ArgumentList = { "trigger" },
                UseShellExecute = false,
            });
            return trigger is not null && trigger.WaitForExit(15000) && trigger.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return false;
        }
    }

    private static void WriteTrofeoHint()
    {
        Console.WriteLine("Thermalright Trofeo Vision models RigGlass drives:");
        Console.WriteLine("  0416:5408   9.16\" and 11.3\"   bulk JPEG   profile thermalright.trofeo-vision");
        Console.WriteLine("  0416:5409   9.16\" LY1 firmware bulk JPEG   profile thermalright.trofeo-vision-ly1");
        Console.WriteLine("  0416:5302   6.86\"             HID JPEG    profile thermalright.trofeo-vision-6.86");
        Console.WriteLine();
        Console.WriteLine("Close Thermalright TRCC first. It keeps the panel to itself.");
        Console.WriteLine("If a cooler screen is detected instead of the 6.86\" panel, run:");
        Console.WriteLine("  rigglass run --profile thermalright.frozen-magic-hid");
    }
}
