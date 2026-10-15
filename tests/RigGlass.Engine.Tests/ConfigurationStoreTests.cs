using RigGlass.Devices;
using RigGlass.Engine.Configuration;
using RigGlass.Engine.Tests.Fakes;

namespace RigGlass.Engine.Tests;

public sealed class ConfigurationStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("rigglass-config-");

    public void Dispose() => _root.Delete(recursive: true);

    [Fact]
    public void ReturnsDefaultsWhenNoFileExists()
    {
        var store = new ConfigurationStore(new AppPaths(_root.FullName));

        var options = store.Load();

        Assert.Empty(store.Problems);
        Assert.Equal(1, options.Version);
        Assert.True(options.Defaults.Enabled);
    }

    [Fact]
    public void RoundTripsEveryConfiguredSetting()
    {
        var paths = new AppPaths(_root.FullName);
        var store = new ConfigurationStore(paths);

        var original = new RigGlassOptions
        {
            DiscoveryIntervalSeconds = 7,
            Defaults = new PanelDefaults { Theme = "minimal", JpegQuality = 72 },
            Sensors = new SensorOptions
            {
                PollIntervalSeconds = 2,
                HistorySeconds = 120,
                DisabledSources = ["hwinfo"],
                AliasOverrides = new Dictionary<string, string> { ["@cpu.temp"] = "cpu/0/temperature/package" },
            },
            Panels =
            [
                new PanelAssignment
                {
                    Key = "usb:0416:5408:sn=TRV-1",
                    DisplayName = "Desk panel",
                    Theme = "starter",
                    Orientation = PanelOrientation.Landscape,
                    Brightness = 60,
                    FramesPerSecond = 15,
                    Variables = new Dictionary<string, string> { ["accent"] = "#ff8800" },
                },
            ],
        };

        store.Save(original);
        var restored = store.Load();

        Assert.Empty(store.Problems);
        Assert.Equal(ConfigurationStore.Serialize(original), ConfigurationStore.Serialize(restored));
    }

    [Fact]
    public void MovesABrokenFileAsideAndKeepsRunning()
    {
        // A truncated or hand-mangled file must not leave the user with dark
        // panels and no explanation.
        var paths = new AppPaths(_root.FullName);
        paths.EnsureCreated();
        File.WriteAllText(paths.ConfigurationFile, "{ this is not json");

        var store = new ConfigurationStore(paths);
        var options = store.Load();

        Assert.Equal(1, options.Version);
        Assert.Contains(store.Problems, p => p.Contains("not valid JSON", StringComparison.Ordinal));
        Assert.True(File.Exists(paths.ConfigurationFile + ".broken"));
    }

    [Fact]
    public void ReportsSettingsThatCannotBeHonoured()
    {
        var options = new RigGlassOptions
        {
            DiscoveryIntervalSeconds = 0,
            Defaults = new PanelDefaults { Brightness = 400, JpegQuality = 0, FramesPerSecond = -1 },
            Sensors = new SensorOptions { PollIntervalSeconds = 10, HistorySeconds = 5 },
            Panels =
            [
                new PanelAssignment { Key = "duplicate" },
                new PanelAssignment { Key = "duplicate" },
            ],
        };

        var problems = options.Validate();

        Assert.Contains(problems, p => p.Contains("discovery interval", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(problems, p => p.Contains("Brightness", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("JPEG quality", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("frame rate", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("share the key", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("history window", StringComparison.Ordinal));
    }

    [Fact]
    public void FallsBackToTheDefaultsForAPanelNobodyHasConfigured()
    {
        var options = new RigGlassOptions
        {
            Defaults = new PanelDefaults { Theme = "minimal", Orientation = PanelOrientation.ReversePortrait },
        };

        var assignment = options.AssignmentFor(TestPanel.Create());

        Assert.Equal("minimal", assignment.Theme);
        Assert.Equal(PanelOrientation.ReversePortrait, assignment.Orientation);
        Assert.Equal("usb:1234:5678:sn=TEST-1", assignment.Key);
    }

    [Fact]
    public void PrefersAnExplicitAssignmentOverTheDefaults()
    {
        var panel = TestPanel.Create();
        var options = new RigGlassOptions { Defaults = new PanelDefaults { Theme = "minimal" } }
            .WithAssignment(new PanelAssignment { Key = panel.StableKey, Theme = "custom" });

        Assert.Equal("custom", options.AssignmentFor(panel).Theme);
    }

    [Fact]
    public void ReplacesRatherThanDuplicatesAnExistingAssignment()
    {
        var panel = TestPanel.Create();
        var options = new RigGlassOptions()
            .WithAssignment(new PanelAssignment { Key = panel.StableKey, Theme = "first" })
            .WithAssignment(new PanelAssignment { Key = panel.StableKey, Theme = "second" });

        Assert.Equal("second", Assert.Single(options.Panels).Theme);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("  ")]
    public void RefusesAThemeIdThatWouldEscapeTheThemesFolder(string themeId)
    {
        // Theme ids arrive from configuration files and from the UI, so a
        // traversal here would let a theme read or write anywhere the user can.
        var paths = new AppPaths(_root.FullName);

        Assert.ThrowsAny<ArgumentException>(() => paths.ThemeDirectory(themeId));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("themes/../../outside")]
    public void StripsAPathOutOfAThemeIdInsteadOfFollowingIt(string themeId)
    {
        var paths = new AppPaths(_root.FullName);

        var directory = paths.ThemeDirectory(themeId);

        Assert.StartsWith(paths.ThemesDirectory + Path.DirectorySeparatorChar, directory, StringComparison.Ordinal);
        Assert.DoesNotContain("..", Path.GetFileName(directory), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsThemeFoldersInsideTheThemesDirectory()
    {
        var paths = new AppPaths(_root.FullName);

        Assert.Equal(Path.Combine(paths.ThemesDirectory, "starter"), paths.ThemeDirectory("starter"));
    }
}
