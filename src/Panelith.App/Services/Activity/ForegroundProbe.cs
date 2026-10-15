using System.Runtime.InteropServices;

namespace Panelith.App.Services.Activity;

/// <summary>Finds the program in front and whether its window fills the monitor.</summary>
public static class ForegroundProbe
{
    /// <summary>The foreground process id and whether its window is fullscreen or borderless fullscreen.</summary>
    public static (int? ProcessId, bool Fullscreen) Read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (null, false);
        }

        var window = NativeMethods.GetForegroundWindow();
        if (window == 0 || window == NativeMethods.GetShellWindow() || window == NativeMethods.GetDesktopWindow())
        {
            return (null, false);
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return (null, false);
        }

        var fullscreen = false;
        if (NativeMethods.GetWindowRect(window, out var rect))
        {
            var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
            var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (monitor != 0 && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                fullscreen = rect.Left <= info.Monitor.Left
                    && rect.Top <= info.Monitor.Top
                    && rect.Right >= info.Monitor.Right
                    && rect.Bottom >= info.Monitor.Bottom;
            }
        }

        return (processId, fullscreen);
    }
}
