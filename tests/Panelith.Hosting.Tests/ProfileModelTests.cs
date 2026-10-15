using Panelith.Engine;
using Panelith.Hosting.Profiles;
using Panelith.Imaging;
using Xunit;

namespace Panelith.Hosting;

public sealed class ProfileModelTests
{
    [Fact]
    public void ScheduleAcrossMidnightCountsTheStartingDay()
    {
        var fridayNight = new ScheduleWindow { Days = [DayOfWeek.Friday], Start = new TimeOnly(23, 0), End = new TimeOnly(7, 0) };

        Assert.True(fridayNight.Contains(new DateTime(2026, 10, 16, 23, 30, 0)));   // Friday 23:30
        Assert.True(fridayNight.Contains(new DateTime(2026, 10, 17, 6, 59, 0)));    // Saturday 06:59
        Assert.False(fridayNight.Contains(new DateTime(2026, 10, 17, 7, 0, 0)));    // Saturday 07:00
        Assert.False(fridayNight.Contains(new DateTime(2026, 10, 16, 2, 0, 0)));    // early Friday belongs to Thursday
        Assert.False(fridayNight.Contains(new DateTime(2026, 10, 17, 23, 30, 0)));  // Saturday night
    }

    [Fact]
    public void SameDayScheduleRespectsDaysAndEnd()
    {
        var work = new ScheduleWindow { Days = ScheduleWindow.Weekdays, Start = new TimeOnly(9, 0), End = new TimeOnly(17, 0) };

        Assert.True(work.Contains(new DateTime(2026, 10, 15, 9, 0, 0)));
        Assert.False(work.Contains(new DateTime(2026, 10, 15, 17, 0, 0)));
        Assert.False(work.Contains(new DateTime(2026, 10, 15, 8, 59, 0)));
        Assert.False(work.Contains(new DateTime(2026, 10, 18, 12, 0, 0))); // Sunday
    }

    [Fact]
    public void EqualStartAndEndMeansAllDay()
    {
        var sunday = new ScheduleWindow { Days = [DayOfWeek.Sunday], Start = new TimeOnly(0, 0), End = new TimeOnly(0, 0) };

        Assert.True(sunday.Contains(new DateTime(2026, 10, 18, 13, 0, 0)));
        Assert.False(sunday.Contains(new DateTime(2026, 10, 19, 13, 0, 0)));
    }

    [Fact]
    public void StarterProfilesCoverEveryActivity()
    {
        var set = DefaultProfiles.Create();

        Assert.Equal(["gaming", "streaming", "creating", "away", "night", "work", "everyday"], set.Profiles.Select(p => p.Id));
        Assert.Equal("everyday", set.Default!.Id);
        Assert.Single(set.Profiles, p => p.IsDefault);
        Assert.False(set.Find("work")!.Enabled);
        Assert.Equal(set.Profiles.Count, set.Profiles.Select(p => p.Triggers.Hotkey).Distinct().Count());
        Assert.All(set.Profiles, p => Assert.NotEmpty(p.Screens));
    }

    [Fact]
    public void LegacyRotationBecomesTheEverydayProfile()
    {
        var set = DefaultProfiles.Create(new LegacyRotation(["neon", "minimal"], 2, Rotate: false));
        var everyday = set.Find("everyday")!;

        Assert.Equal(["neon", "minimal"], everyday.Screens);
        Assert.False(everyday.Rotate);
        Assert.Equal(5, everyday.RotationSeconds);
    }

    [Fact]
    public void EmptyLegacyRotationKeepsTheStarterScreens()
    {
        var everyday = DefaultProfiles.Create(new LegacyRotation([], 30, Rotate: true)).Find("everyday")!;

        Assert.Equal(["aurora", "overview", "pulse"], everyday.Screens);
    }

    [Fact]
    public void DefaultFallsBackToFirstEnabledProfile()
    {
        var set = new ProfileSet
        {
            Profiles =
            [
                new Profile { Id = "a", Enabled = false },
                new Profile { Id = "b" },
                new Profile { Id = "c", IsDefault = true, Enabled = false },
            ],
        };

        Assert.Equal("b", set.Default!.Id);
        Assert.Equal("c", set.Find("C")!.Id);
        Assert.Null(set.Find(null));
    }

    [Fact]
    public void StoreRoundTripsAndFallsBackOnBadFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "panelith-profiles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(new AppPaths(root));
            Assert.Equal(7, store.Load().Profiles.Count);

            var edited = DefaultProfiles.Create() with { Pinned = "night", Automatic = false };
            store.Save(edited);
            var loaded = store.Load();
            Assert.Equal("night", loaded.Pinned);
            Assert.False(loaded.Automatic);
            Assert.Equal(new TimeOnly(23, 0), loaded.Find("night")!.Triggers.Schedules[0].Start);
            Assert.Equal(ScheduleWindow.Weekdays, loaded.Find("work")!.Triggers.Schedules[0].Days);

            File.WriteAllText(store.File, "{ not json");
            Assert.Equal(7, store.Load().Profiles.Count);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void SteamLibrariesAreReadFromTheVdf()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], GameLibraryLocator.ParseSteamLibraries(vdf));
    }

    [Fact]
    public void EpicManifestGivesTheInstallFolder()
    {
        Assert.Equal(@"E:\Epic\Fortnite", GameLibraryLocator.ParseEpicManifest("""{ "DisplayName": "Fortnite", "InstallLocation": "E:\\Epic\\Fortnite" }"""));
        Assert.Null(GameLibraryLocator.ParseEpicManifest("""{ "DisplayName": "Nothing" }"""));
        Assert.Null(GameLibraryLocator.ParseEpicManifest("not json"));
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\game.exe", true)]
    [InlineData(@"d:\steamlibrary\STEAMAPPS\common\Game\game.exe", true)]
    [InlineData(@"D:\SteamLibrary\steamapps\common", false)]
    [InlineData(@"D:\SteamLibrary\steamapps\commonplace\x.exe", false)]
    [InlineData(@"C:\Windows\explorer.exe", false)]
    [InlineData(null, false)]
    public void IsUnderMatchesWholeFolders(string? path, bool expected)
    {
        Assert.Equal(expected, GameLibraryLocator.IsUnder(path, [@"D:\SteamLibrary\steamapps\common\", @"E:\XboxGames"]));
    }

    [Fact]
    public void DimScalesColourAndKeepsAlpha()
    {
        var pixels = new byte[] { 200, 100, 50, 255, 255, 255, 255, 128 };
        var frame = FrameBuffer.Wrap(2, 1, pixels);

        LiveThemeRendererFactory.ApplyDim(frame, 0);
        Assert.Equal([200, 100, 50, 255, 255, 255, 255, 128], pixels);

        LiveThemeRendererFactory.ApplyDim(frame, 0.5);
        Assert.Equal([100, 50, 25, 255, 127, 127, 127, 128], pixels);

        LiveThemeRendererFactory.ApplyDim(frame, 1);
        Assert.Equal([0, 0, 0, 255, 0, 0, 0, 128], pixels);
    }
}
