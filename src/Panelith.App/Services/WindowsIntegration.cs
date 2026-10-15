using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace Panelith.App.Services;

/// <summary>Administrator rights and start-with-Windows.</summary>
/// <remarks>
/// Most sensors and the panel itself work as a standard user, but LibreHardwareMonitor
/// needs its kernel driver for CPU temperature, clocks and power on many CPUs,
/// including every Ryzen. Starting elevated at sign-in without a UAC prompt each
/// time is only possible through a scheduled task, so that is what "start with
/// Windows" creates when the app is elevated; otherwise it falls back to the
/// per-user Run key.
/// </remarks>
public static class WindowsIntegration
{
    private const string TaskName = "Panelith";
    private const string PreviousTaskName = "RigGlass";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The argument that starts the app hidden in the notification area.</summary>
    public const string MinimizedArgument = "--minimized";

    /// <summary>The argument a relaunched instance uses to wait for the previous one to exit.</summary>
    public const string RestartArgument = "--restarted";

    /// <summary>Whether this process runs with administrator rights.</summary>
    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>Whether elevation and start-up registration are available here.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The official PawnIO site.</summary>
    public const string PawnIOUrl = "https://pawnio.eu/";

    /// <summary>The official PawnIO setup that ships with Panelith, or null when this build has none.</summary>
    public static string? BundledPawnIOSetup =>
        Path.Combine(AppContext.BaseDirectory, "tools", "PawnIO_setup.exe") is var path && File.Exists(path) ? path : null;

    /// <summary>
    /// Installs PawnIO with the bundled setup, asking Windows for permission
    /// when Panelith is not elevated. Opens the PawnIO site when no setup is
    /// bundled. Returns a problem to show, or null.
    /// </summary>
    public static async Task<string?> InstallPawnIOAsync()
    {
        if (BundledPawnIOSetup is not { } setup)
        {
            Process.Start(new ProcessStartInfo(PawnIOUrl) { UseShellExecute = true });
            return "The PawnIO setup is not included in this copy of Panelith, so its site was opened instead.";
        }

        try
        {
            var start = new ProcessStartInfo(setup, "-install -silent") { UseShellExecute = true };
            if (!IsElevated)
            {
                start.Verb = "runas";
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return "The PawnIO setup did not start.";
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode == 0 ? null : $"The PawnIO setup stopped with code {process.ExitCode}.";
        }
        catch (Win32Exception)
        {
            return "Windows did not get permission to install PawnIO.";
        }
    }

    /// <summary>Whether Windows Media Foundation, which video screens need, is missing (Windows N editions).</summary>
    public static bool IsMediaFoundationMissing =>
        OperatingSystem.IsWindows() && !File.Exists(Path.Combine(Environment.SystemDirectory, "mfplat.dll"));

    /// <summary>
    /// Adds the Windows Media Feature Pack, asking for permission. Returns a
    /// problem to show, or null. Windows may need a restart before videos work.
    /// </summary>
    public static async Task<string?> AddMediaFeaturePackAsync()
    {
        try
        {
            var start = new ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "dism.exe"),
                "/Online /Add-Capability /CapabilityName:Media.MediaFeaturePack~~~~0.0.1.0 /NoRestart /Quiet")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            if (!IsElevated)
            {
                start.Verb = "runas";
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return "Windows could not start adding the Media Feature Pack.";
            }

            await process.WaitForExitAsync().ConfigureAwait(true);
            return process.ExitCode switch
            {
                0 => null,
                3010 => "The Media Feature Pack was added. Restart Windows to finish.",
                _ => $"Adding the Media Feature Pack stopped with code {process.ExitCode}. It can also be added in Settings, Apps, Optional features.",
            };
        }
        catch (Win32Exception)
        {
            return "Windows did not get permission to add the Media Feature Pack.";
        }
    }

    /// <summary>
    /// Whether the PawnIO driver is installed. LibreHardwareMonitor reads CPU
    /// temperature, clocks and power through it.
    /// </summary>
    public static bool IsPawnIOInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var uninstall = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
        return uninstall is not null || service is not null;
    }

    /// <summary>
    /// Starts an elevated copy of the app. Returns false when the user declined
    /// the UAC prompt.
    /// </summary>
    public static bool RelaunchElevated()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, RestartArgument) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the app is set to start at sign-in, and whether that start is elevated.</summary>
    public static (bool Enabled, bool Elevated) StartupState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, false);
        }

        if (RunSchtasks($"/Query /TN \"{TaskName}\"") == 0)
        {
            return (true, true);
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return (key?.GetValue(TaskName) is string, false);
    }

    /// <summary>
    /// Registers or removes start at sign-in. Returns a message describing
    /// anything that went wrong, or null.
    /// </summary>
    public static string? SetStartup(bool enabled)
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return "Start with Windows is only available on Windows.";
        }

        // Clear both forms first so switching between elevated and not never leaves two.
        if (IsElevated)
        {
            RunSchtasks($"/Delete /TN \"{TaskName}\" /F");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            key.DeleteValue(TaskName, throwOnMissingValue: false);
            if (enabled && !IsElevated)
            {
                key.SetValue(TaskName, $"\"{exe}\" {MinimizedArgument}");
                return null;
            }
        }

        if (!enabled)
        {
            return StartupState().Enabled
                ? "The elevated start-up task can only be removed while Panelith runs as administrator."
                : null;
        }

        if (ElevatedStartupProblem(exe) is { } problem)
        {
            // Never run elevated from a folder a standard user could swap files into:
            // that would hand administrator rights to whoever replaced Panelith.exe.
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(TaskName, $"\"{exe}\" {MinimizedArgument}");
            return $"{problem} Panelith will start at sign-in without administrator rights. Install it with the Panelith installer to start it elevated.";
        }

        var exitCode = RunSchtasks(
            $"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" {MinimizedArgument}\" /SC ONLOGON /RL HIGHEST /F");
        return exitCode == 0 ? null : $"Windows refused to create the start-up task (schtasks exit code {exitCode}).";
    }

    /// <summary>
    /// Checks that the elevated start-up task still runs this copy of Panelith
    /// from a safe folder. Returns a message describing the problem, or null.
    /// </summary>
    public static string? CheckStartupTask()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } exe)
        {
            return null;
        }

        var (code, xml) = RunSchtasksWithOutput($"/Query /TN \"{TaskName}\" /XML");
        if (code != 0 || TaskCommand(xml) is not { } command)
        {
            return null;
        }

        var fix = IsElevated ? "Choose Repair to point it at this copy." : "Restart as administrator to repair it.";
        if (!string.Equals(Path.GetFullPath(command), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase))
        {
            return $"The elevated start-up task runs \"{command}\", not this copy of Panelith. {fix}";
        }

        return ElevatedStartupProblem(exe) is { } problem
            ? $"{problem} The elevated start-up task is unsafe. {fix}"
            : null;
    }

    /// <summary>
    /// Replaces start at sign-in registered under the app's previous name with
    /// the current one. The old elevated task can only be removed while elevated;
    /// the installer removes it otherwise. Returns true when anything was moved.
    /// </summary>
    public static bool MigrateLegacyStartup()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var hadTask = IsElevated && RunSchtasks($"/Query /TN \"{PreviousTaskName}\"") == 0;
        if (hadTask)
        {
            RunSchtasks($"/Delete /TN \"{PreviousTaskName}\" /F");
        }

        bool hadRunValue;
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            hadRunValue = key.GetValue(PreviousTaskName) is string;
            key.DeleteValue(PreviousTaskName, throwOnMissingValue: false);
        }

        if (!hadTask && !hadRunValue)
        {
            return false;
        }

        if (!StartupState().Enabled)
        {
            SetStartup(enabled: true);
        }

        return true;
    }

    /// <summary>Re-creates start at sign-in for this copy of Panelith. Needs administrator rights.</summary>
    public static string? RepairStartup() =>
        IsElevated ? SetStartup(enabled: true) : "Repairing the start-up task needs administrator rights.";

    /// <summary>The program a scheduled task's XML definition runs, without quotes.</summary>
    internal static string? TaskCommand(string xml)
    {
        var match = System.Text.RegularExpressions.Regex.Match(xml, "<Command>(.*?)</Command>", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!match.Success)
        {
            return null;
        }

        var command = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim().Trim('"');
        return command.Length == 0 ? null : command;
    }

    /// <summary>
    /// Why <paramref name="exe"/> must not be started elevated automatically, or
    /// null when it is safe: it lives under Program Files and neither its folder
    /// nor the program files in it can be changed by standard users.
    /// </summary>
    public static string? ElevatedStartupProblem(string exe)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Elevated start-up is only available on Windows.";
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(exe));
        if (folder is null)
        {
            return "Panelith's folder could not be determined.";
        }

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ];
        if (!IsUnder(folder, roots))
        {
            return $"Panelith is running from \"{folder}\", outside Program Files.";
        }

        try
        {
            if (UserWritable(new DirectoryInfo(folder).GetAccessControl()))
            {
                return $"Standard users can change files in \"{folder}\".";
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var extension = Path.GetExtension(file);
                if ((extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    && UserWritable(new FileInfo(file).GetAccessControl()))
                {
                    return $"Standard users can change \"{Path.GetFileName(file)}\" in Panelith's folder.";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"The permissions on \"{folder}\" could not be checked ({ex.Message}).";
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> is inside one of <paramref name="roots"/>.</summary>
    internal static bool IsUnder(string path, IEnumerable<string> roots)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Everyone, Authenticated Users, Interactive and Users: groups every standard user belongs to.</summary>
    private static readonly HashSet<string> StandardUserSids = ["S-1-1-0", "S-1-5-11", "S-1-5-4", "S-1-5-32-545"];

    /// <summary>Whether an ACL allows any standard user to modify the object.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static bool UserWritable(FileSystemSecurity security)
    {
        // Rights that would let someone replace or alter a file.
        const FileSystemRights WriteRights =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteAttributes
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow
                && (rule.FileSystemRights & WriteRights) != 0
                && StandardUserSids.Contains(rule.IdentityReference.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static int RunSchtasks(string arguments) => RunSchtasksWithOutput(arguments).ExitCode;

    private static (int ExitCode, string Output) RunSchtasksWithOutput(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return (-1, string.Empty);
            }

            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit(10_000);
            return process.HasExited ? (process.ExitCode, output.Result) : (-1, string.Empty);
        }
        catch (Win32Exception)
        {
            return (-1, string.Empty);
        }
    }
}
