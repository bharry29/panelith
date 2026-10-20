using System.Diagnostics;
using Panelith.App.Services;

namespace Panelith.App;

public sealed class UpdateInstallerLaunchTests
{
    [Fact]
    public void AnExtensionlessDownloadIsGivenAnExeNameWindowsWillRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panelith-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var download = Path.Combine(folder, "226e09ec-9d50-480e-a192-487f7b801be3");
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), download);
        try
        {
            string launch;
            using (var locked = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                launch = UpdateService.ExecutablePath(download);
                using var launchLock = new FileStream(launch, FileMode.Open, FileAccess.Read, FileShare.Read);
                Assert.EndsWith(".exe", launch, StringComparison.OrdinalIgnoreCase);
                Assert.Throws<IOException>(() => File.WriteAllText(launch, "swapped"));
            }

            using var process = Process.Start(new ProcessStartInfo(launch) { UseShellExecute = true, CreateNoWindow = true });
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(10000));
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ADownloadThatIsAlreadyAnExeIsLeftAlone()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panelith-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var exe = Path.Combine(folder, "Panelith-setup.exe");
        File.WriteAllText(exe, "already an exe");
        try
        {
            Assert.Equal(exe, UpdateService.ExecutablePath(exe));
            Assert.False(File.Exists(exe + ".exe"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
