using Panelith.Engine;
using Panelith.Themes;

namespace Panelith.Hosting;

public sealed class ScreenWorkshopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panelith-workshop-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ThemeLibrary _library;
    private readonly ScreenWorkshop _workshop;

    public ScreenWorkshopTests()
    {
        _paths = new AppPaths(_root);
        Directory.CreateDirectory(_paths.ThemesDirectory);
        _library = new ThemeLibrary(_paths);
        _workshop = new ScreenWorkshop(_library, _paths);
    }

    [Theory]
    [InlineData("My Lake Video!", "my-lake-video")]
    [InlineData("  ..\\..\\evil  ", "evil")]
    [InlineData("???", "screen")]
    [InlineData("Ünïcode Ok", "n-code-ok")]
    public void NamesBecomeSafeFolderNames(string name, string expected) =>
        Assert.Equal(expected, ScreenWorkshop.Slug(name));

    [Fact]
    public void ANewScreenNeverReusesATakenName()
    {
        var (first, _) = _workshop.NewScreenFolder("Lake");
        var (second, folder) = _workshop.NewScreenFolder("Lake");
        var (bundled, _) = _workshop.NewScreenFolder("Clock");

        Assert.Equal("lake", first);
        Assert.Equal("lake-2", second);
        Assert.Equal("clock-2", bundled);
        Assert.StartsWith(Path.GetFullPath(_paths.ThemesDirectory), folder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AVideoScreenRoundTripsAndValidates()
    {
        var theme = ScreenWorkshop.VideoScreen("lake", "Lake", 1920, 480, "clip.ptclip", 0.5d, VideoOverlay.Readings);
        var (_, folder) = _workshop.NewScreenFolder("lake");
        _workshop.Save(theme, folder);

        var loaded = _library.Load("lake");
        Assert.Equal("clip.ptclip", loaded.Background.Video);
        Assert.Equal(0.5d, loaded.Background.VideoSpeed);
        Assert.Single(loaded.Widgets);
        Assert.DoesNotContain(ThemeValidator.Validate(loaded, checkAssets: false), d => d.Severity == ThemeDiagnosticSeverity.Error);
    }

    [Fact]
    public void ABundledScreenIsCopiedBeforeItIsChanged()
    {
        var (id, folder, theme) = _workshop.Editable("clock");

        Assert.Equal("clock-custom", id);
        Assert.Equal("Clock (custom)", theme.Name);
        Assert.True(File.Exists(Path.Combine(folder, ThemeSchema.FileName)));
        Assert.Equal("clock-custom", _library.Load(id).Id);

        var again = _workshop.Editable(id);
        Assert.Equal(id, again.Id);
        Assert.Equal(folder, again.Folder);
    }

    [Fact]
    public void BundledScreensCannotBeWrittenOrDeleted()
    {
        var bundled = Path.GetDirectoryName(_library.Resolve("clock"))!;

        Assert.Throws<UnauthorizedAccessException>(() => _workshop.Save(_library.Load("clock"), bundled));
        Assert.Throws<UnauthorizedAccessException>(() => _workshop.Delete("clock"));
        Assert.Throws<UnauthorizedAccessException>(() => _workshop.Save(_library.Load("clock"), _paths.ThemesDirectory));
    }

    [Fact]
    public void AScreenWithAFileInUseStaysWholeWhenDeleted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to delete open files.");
        var (id, folder, theme) = _workshop.Editable("clock");
        var clip = Path.Combine(folder, "clip.ptclip");
        File.WriteAllText(clip, "x");

        using (new FileStream(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => _workshop.Delete(id));
            Assert.True(File.Exists(Path.Combine(folder, "theme.json")));
        }

        _workshop.Delete(id);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void AWallpaperReplacesTheOtherKindAndOldFilesAreRemoved()
    {
        var (_, folder, theme) = _workshop.Editable("clock");
        File.WriteAllText(Path.Combine(folder, "wallpaper-old.jpg"), "x");
        var clip = ScreenWorkshop.FreshAssetName(folder, "wallpaper", ".ptclip");
        File.WriteAllText(Path.Combine(folder, clip), "x");

        var withPicture = ScreenWorkshop.WithWallpaper(theme with { Background = theme.Background with { Image = "wallpaper-old.jpg" } }, null, clip, 1.5d, "#00000059");
        ScreenWorkshop.RemoveUnusedWallpapers(folder, withPicture);

        Assert.Null(withPicture.Background.Image);
        Assert.Equal(clip, withPicture.Background.Video);
        Assert.False(File.Exists(Path.Combine(folder, "wallpaper-old.jpg")));
        Assert.True(File.Exists(Path.Combine(folder, clip)));
        Assert.Throws<ArgumentException>(() => ScreenWorkshop.WithWallpaper(theme, "a.jpg", "b.ptclip", 1d, null));

        var plain = ScreenWorkshop.WithoutWallpaper(withPicture);
        Assert.Null(plain.Background.Video);
        Assert.Null(plain.Background.Scrim);
    }

    [Fact]
    public void DeletingRemovesTheUsersScreen()
    {
        var (id, folder) = _workshop.NewScreenFolder("Gone");
        _workshop.Save(ScreenWorkshop.VideoScreen(id, "Gone", 480, 320, "clip.ptclip", 1d, VideoOverlay.None), folder);

        _workshop.Delete(id);

        Assert.False(Directory.Exists(folder));
        Assert.Null(_library.Resolve(id));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
