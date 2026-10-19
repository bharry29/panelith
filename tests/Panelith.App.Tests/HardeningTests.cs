using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Panelith.App.Services;

namespace Panelith.App;

public sealed class HardeningTests
{
    [Fact]
    public void IsUnderMatchesWholeFolderNamesOnly()
    {
        string[] roots = [@"C:\Program Files"];

        Assert.True(WindowsIntegration.IsUnder(@"C:\Program Files\Panelith", roots));
        Assert.True(WindowsIntegration.IsUnder(@"c:\program files\Panelith\sub", roots));
        Assert.False(WindowsIntegration.IsUnder(@"C:\Program Files Evil\Panelith", roots));
        Assert.False(WindowsIntegration.IsUnder(@"C:\Users\me\Desktop\Panelith", roots));
        Assert.False(WindowsIntegration.IsUnder(@"C:\Program Files\..\Users\Panelith", roots));
    }

    [Fact]
    public void AFolderStandardUsersCanWriteToIsUnsafe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var readOnly = new DirectorySecurity();
        readOnly.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        readOnly.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, AccessControlType.Allow));
        Assert.False(WindowsIntegration.UserWritable(readOnly));

        var writable = new DirectorySecurity();
        writable.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        Assert.True(WindowsIntegration.UserWritable(writable));

        var everyoneAppend = new DirectorySecurity();
        everyoneAppend.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.AppendData, AccessControlType.Allow));
        Assert.True(WindowsIntegration.UserWritable(everyoneAppend));
    }

    [Fact]
    public void ACopyOutsideProgramFilesMayNotStartElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exe = Path.Combine(Path.GetTempPath(), "Panelith.exe");
        Assert.NotNull(WindowsIntegration.ElevatedStartupProblem(exe));
    }

    [Fact]
    public void TheTaskCommandIsReadFromItsXml()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-16"?>
            <Task><Actions Context="Author"><Exec>
              <Command>"C:\Program Files\Panelith\Panelith.exe"</Command>
              <Arguments>--minimized</Arguments>
            </Exec></Actions></Task>
            """;

        Assert.Equal(@"C:\Program Files\Panelith\Panelith.exe", WindowsIntegration.TaskCommand(xml));
        Assert.Null(WindowsIntegration.TaskCommand("<Task />"));
    }

    [Fact]
    public void SchtasksIsRunFromSystem32AndNotFoundOnThePath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Creating the start-up task happens while elevated, so resolving the
        // program through PATH would hand administrator rights to whatever a
        // writable entry there contains.
        var path = WindowsIntegration.SchtasksPath;

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void AsksForAdministratorRightsOnEveryLaunchUntilTurnedOff()
    {
        // On by default, including for settings saved before the option existed.
        Assert.True(new AppSettings().AlwaysRunAsAdministrator);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}", new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AlwaysRunAsAdministrator);

        Assert.True(WindowsIntegration.ShouldRelaunchElevated(isElevated: false, alwaysRunAsAdministrator: true));
        Assert.False(WindowsIntegration.ShouldRelaunchElevated(isElevated: true, alwaysRunAsAdministrator: true));
        Assert.False(WindowsIntegration.ShouldRelaunchElevated(isElevated: false, alwaysRunAsAdministrator: false));
        Assert.False(WindowsIntegration.ShouldRelaunchElevated(isElevated: true, alwaysRunAsAdministrator: false));
    }

    [Fact]
    public void ElevatedRelaunchStaysHiddenWhenStartedHidden()
    {
        Assert.Equal("--restarted --minimized", WindowsIntegration.RelaunchArguments([WindowsIntegration.MinimizedArgument]));
        Assert.Equal("--restarted", WindowsIntegration.RelaunchArguments([]));
    }

    [Fact]
    public void ThreeErrorsInAMinuteMakeABurst()
    {
        var burst = new ErrorBurst(3, TimeSpan.FromSeconds(60));
        var start = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(burst.Record(start));
        Assert.False(burst.Record(start.AddSeconds(50)));
        Assert.False(burst.Record(start.AddSeconds(70)));   // the first one has expired
        Assert.True(burst.Record(start.AddSeconds(75)));
        Assert.False(burst.Record(start.AddSeconds(76)));   // counting starts over
    }

    [Fact]
    public void LogsHideTheUserProfileFolder()
    {
        Assert.Equal(
            @"Could not read %USERPROFILE%\AppData\Roaming\Panelith\app.json",
            AppLog.Redact(@"Could not read C:\Users\Tony\AppData\Roaming\Panelith\app.json", @"C:\Users\Tony"));
        Assert.Equal("nothing to hide", AppLog.Redact("nothing to hide", @"C:\Users\Tony"));
    }

    [Fact]
    public void LogFilesRollOverAndTheOldestIsDropped()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panelith-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var day = new DateOnly(2026, 10, 15);
            for (var index = 0; index < AppLog.FilesPerDay; index++)
            {
                File.WriteAllText(AppLog.FileFor(folder, day, index), index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            AppLog.Roll(folder, day);

            Assert.False(File.Exists(AppLog.FileFor(folder, day)));
            Assert.Equal("0", File.ReadAllText(AppLog.FileFor(folder, day, 1)));
            Assert.Equal("3", File.ReadAllText(AppLog.FileFor(folder, day, AppLog.FilesPerDay - 1)));
            Assert.Equal(AppLog.FilesPerDay - 1, Directory.GetFiles(folder).Length);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void OldLogFilesAreDeleted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panelith-logs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var old = Path.Combine(folder, "panelith-20250101.log");
            var recent = Path.Combine(folder, "panelith-20261015.log");
            var other = Path.Combine(folder, "notes.txt");
            File.WriteAllText(old, "old");
            File.WriteAllText(recent, "recent");
            File.WriteAllText(other, "keep");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

            AppLog.DeleteOldFiles(folder, DateTime.Now.AddDays(-AppLog.RetentionDays));

            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            Assert.True(File.Exists(other));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
