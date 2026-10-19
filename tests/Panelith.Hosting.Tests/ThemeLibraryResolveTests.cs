using Panelith.Engine;

namespace Panelith.Hosting;

public sealed class ThemeLibraryResolveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panelith-library-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ThemeLibrary _library;

    public ThemeLibraryResolveTests()
    {
        _paths = new AppPaths(_root);
        Directory.CreateDirectory(_paths.ThemesDirectory);
        _library = new ThemeLibrary(_paths);
    }

    [Fact]
    public void AnIdResolvesToTheThemeOfThatName()
    {
        var folder = Path.Combine(_paths.ThemesDirectory, "rig");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "theme.json"), "{}");

        Assert.Equal(Path.Combine(folder, "theme.json"), _library.Resolve("rig"));
    }

    [Fact]
    public void AnIdCannotClimbOutOfTheThemesFolder()
    {
        // Screen ids come from settings and profiles, which are files on disk. One
        // holding ".." must not turn into a theme loaded from somewhere else.
        var outside = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "theme.json"), "{}");

        Assert.Null(_library.Resolve("../elsewhere"));
        Assert.Null(_library.Resolve(@"..\elsewhere"));
    }

    [Fact]
    public void AnIdNamingASiblingOfTheThemesFolderIsNotFound()
    {
        var sibling = _paths.ThemesDirectory + "-other";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "theme.json"), "{}");

        Assert.Null(_library.Resolve(".."));
        Assert.Null(_library.Resolve("../" + Path.GetFileName(sibling)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
