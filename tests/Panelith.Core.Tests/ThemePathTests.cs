using Panelith.Themes;

namespace Panelith;

public sealed class ThemePathTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "panelith-theme-path");
    private static readonly string Theme = Path.Combine(Base, "rig");

    [Theory]
    [InlineData("background.png")]
    [InlineData("assets/background.png")]
    [InlineData("assets/deeper/background.png")]
    [InlineData("./assets/background.png")]
    [InlineData("assets/../background.png")]
    public void PathsInsideTheThemeFolderResolve(string relative)
    {
        Assert.True(ThemePath.TryResolveInside(Theme, relative, out var full));
        Assert.StartsWith(Theme + Path.DirectorySeparatorChar, full, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../other/secret.png")]
    [InlineData("../../secret.png")]
    [InlineData("assets/../../other/secret.png")]
    public void PathsThatClimbOutOfTheThemeFolderAreRefused(string relative)
    {
        Assert.False(ThemePath.TryResolveInside(Theme, relative, out var full));
        Assert.Equal(string.Empty, full);
    }

    [Fact]
    public void ASiblingFolderSharingAPrefixIsOutside()
    {
        // The case a plain StartsWith comparison gets wrong: "rig-other" starts
        // with "rig", but it is a different theme's folder.
        var sibling = Path.Combine(Base, "rig-other", "secret.png");

        Assert.False(ThemePath.TryResolveInside(Theme, "../rig-other/secret.png", out _));
        Assert.False(ThemePath.IsInside(Theme, sibling));
        Assert.True(ThemePath.IsInside(Theme, Path.Combine(Theme, "secret.png")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NothingIsNotAPath(string? relative)
    {
        Assert.False(ThemePath.TryResolveInside(Theme, relative!, out _));
        Assert.False(ThemePath.IsInside(relative!, Theme));
    }

    [Fact]
    public void AnAbsolutePathIsRefusedEvenThoughItNormalisesCleanly()
    {
        Assert.False(ThemePath.TryResolveInside(Theme, Path.Combine(Base, "other", "secret.png"), out _));
    }

    [Fact]
    public void WindowsDriveRelativeAndUncPathsAreRefused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(ThemePath.TryResolveInside(@"C:\themes\rig", @"C:background.png", out _));
        Assert.False(ThemePath.TryResolveInside(@"C:\themes\rig", @"\\server\share\secret.png", out _));
        Assert.False(ThemePath.TryResolveInside(@"C:\themes\rig", @"..\..\windows\system32\config\sam", out _));
        Assert.False(ThemePath.TryResolveInside(@"C:\themes\rig", @"..\rig-other\secret.png", out _));
        Assert.True(ThemePath.TryResolveInside(@"C:\themes\rig", @"assets\deeper\background.png", out _));
    }

    [Fact]
    public void TheFolderItselfCountsAsInsideHoweverItIsSpelled()
    {
        Assert.True(ThemePath.IsInside(Theme, Theme));
        Assert.True(ThemePath.IsInside(Theme + Path.DirectorySeparatorChar, Theme));
        Assert.True(ThemePath.IsInside(Theme, Theme + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void ValidationRejectsAnAssetInASiblingThemeFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "panelith-sibling-" + Guid.NewGuid().ToString("N"));
        var theme = Path.Combine(root, "rig");
        var sibling = Path.Combine(root, "rig-other");
        try
        {
            Directory.CreateDirectory(theme);
            Directory.CreateDirectory(sibling);

            // A real file, so the only thing that can fail the asset is the
            // containment rule rather than the file simply being missing.
            File.WriteAllText(Path.Combine(sibling, "secret.png"), "not really a png");

            var document = ThemeSerializer.Deserialize(
                """
                {
                  "schema": 1,
                  "canvas": { "width": 100, "height": 100 },
                  "widgets": [
                    { "type": "image", "x": 0, "y": 0, "width": 10, "height": 10, "source": "../rig-other/secret.png" }
                  ]
                }
                """,
                theme);

            var diagnostics = ThemeValidator.Validate(document);
            Assert.Contains(
                diagnostics,
                d => d.Severity == ThemeDiagnosticSeverity.Error && d.Message.Contains("outside the theme folder", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
