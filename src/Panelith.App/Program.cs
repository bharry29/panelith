using System.Security.AccessControl;
using System.Security.Principal;
using Avalonia;
using Panelith.App.Services;
using Panelith.Hosting;

namespace Panelith.App;

internal static class Program
{
    private const string InstanceName = @"Local\Panelith.App";
    private const string ShowSignalName = @"Local\Panelith.App.Show";

    /// <summary>Signalled by a second launch so the running copy brings its window forward.</summary>
    internal static EventWaitHandle? ShowSignal { get; private set; }

    internal static string[] Arguments { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        Arguments = args;
        ProcessHardening.RestrictDllSearch();
        if (OperatingSystem.IsWindows() && MediaJobs.TryRunChild(args, out var exitCode))
        {
            return exitCode;
        }

        // Only one copy may own the USB panel. A relaunch (for example after
        // "restart as administrator") waits for the previous copy to let go.
        using var instance = TryAcquireInstance(waitForPrevious: args.Contains(WindowsIntegration.RestartArgument));
        if (instance is null)
        {
            SignalRunningInstance();
            return 0;
        }

        ShowSignal = CreateShowSignal();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            ShowSignal?.Dispose();
            instance.ReleaseMutex();
        }
    }

    /// <summary>Used by the designer and by tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static Mutex? TryAcquireInstance(bool waitForPrevious)
    {
        Mutex mutex;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(CurrentUser(), MutexRights.FullControl, AccessControlType.Allow));
                security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                mutex = MutexAcl.Create(initiallyOwned: false, InstanceName, out _, security);
            }
            else
            {
                mutex = new Mutex(initiallyOwned: false, InstanceName);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Created by an elevated copy this standard-user process cannot open.
            return null;
        }

        try
        {
            if (mutex.WaitOne(waitForPrevious ? TimeSpan.FromSeconds(20) : TimeSpan.Zero))
            {
                return mutex;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous copy crashed; the mutex is ours now.
            return mutex;
        }

        mutex.Dispose();
        return null;
    }

    private static EventWaitHandle? CreateShowSignal()
    {
        // Named events are Windows-only; elsewhere a second launch just exits.
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var security = new EventWaitHandleSecurity();
            security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser(), EventWaitHandleRights.FullControl, AccessControlType.Allow));
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowSignalName, out _, security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The signed-in user. The mutex and event grant access to this account
    /// alone, elevated or not, so other accounts cannot open, hold or signal them.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new UnauthorizedAccessException("The current user has no security identifier.");
    }

    private static void SignalRunningInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal))
            {
                using (signal)
                {
                    signal.Set();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated; it cannot be signalled from here.
        }
    }
}
