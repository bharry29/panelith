using System.Runtime.InteropServices;

namespace Panelith.App.Services.Activity;

/// <summary>How long the user has been away, and whether Windows is locked.</summary>
public static class IdleProbe
{
    private const uint DesktopReadObjects = 0x0001;

    /// <summary>Seconds since the last keyboard or mouse input.</summary>
    public static double IdleSeconds()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        var info = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return 0;
        }

        var elapsed = unchecked((uint)Environment.TickCount - info.Time);
        return elapsed / 1000d;
    }

    /// <summary>Whether the lock screen (or another secure desktop) is up.</summary>
    /// <remarks>
    /// While locked, the input desktop is Winlogon's, which a normal process
    /// cannot open; when it can be opened, its name says which one it is.
    /// </remarks>
    public static unsafe bool IsLocked()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var desktop = NativeMethods.OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
        {
            return true;
        }

        try
        {
            var buffer = stackalloc char[64];
            return NativeMethods.GetUserObjectInformation(desktop, NativeMethods.UoiName, buffer, 128, out _)
                && !string.Equals(new string(buffer).TrimEnd('\0'), "Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NativeMethods.CloseDesktop(desktop);
        }
    }
}
