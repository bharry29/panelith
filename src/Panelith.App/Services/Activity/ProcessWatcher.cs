using System.Diagnostics;
using System.Runtime.Versioning;
using Panelith.Hosting.Profiles;

namespace Panelith.App.Services.Activity;

/// <summary>Lists running programs with their executable paths.</summary>
/// <remarks>
/// Paths are read once per process through <c>PROCESS_QUERY_LIMITED_INFORMATION</c>,
/// which Windows grants for most processes even to a standard user; the few it
/// refuses (protected and some elevated ones) are listed by name only.
/// </remarks>
public sealed class ProcessWatcher
{
    private readonly Dictionary<int, ProcessInfo> _known = [];

    /// <summary>Every running program.</summary>
    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return [.. _known.Values];
        }

        var seen = new HashSet<int>();
        var result = new List<ProcessInfo>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                int id;
                string name;
                try
                {
                    id = process.Id;
                    name = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    continue;
                }

                if (id <= 4)
                {
                    continue;
                }

                seen.Add(id);
                if (!_known.TryGetValue(id, out var info) || !string.Equals(info.Name, name, StringComparison.Ordinal))
                {
                    info = new ProcessInfo(id, name, OperatingSystem.IsWindows() ? QueryPath(id) : null);
                    _known[id] = info;
                }

                result.Add(info);
            }
        }

        foreach (var gone in _known.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _known.Remove(gone);
        }

        return result;
    }

    /// <summary>Looks up a program by process id in the last snapshot.</summary>
    public ProcessInfo? Find(int id) => _known.GetValueOrDefault(id);

    [SupportedOSPlatform("windows")]
    internal static unsafe string? QueryPath(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return NativeMethods.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
