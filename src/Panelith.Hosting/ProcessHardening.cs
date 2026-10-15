using System.Runtime.InteropServices;

namespace Panelith.Hosting;

/// <summary>Process-wide safety settings to apply first thing in <c>Main</c>.</summary>
public static partial class ProcessHardening
{
    /// <summary>The application folder, System32 and folders added with AddDllDirectory.</summary>
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;

    /// <summary>
    /// Stops Windows from loading DLLs by bare name from the current directory
    /// or from <c>PATH</c>, so a planted DLL there is never picked up, even
    /// when Panelith runs elevated. Returns false when the call failed.
    /// </summary>
    public static bool RestrictDllSearch()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        return SetDefaultDllDirectories(LoadLibrarySearchDefaultDirs);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);
}
