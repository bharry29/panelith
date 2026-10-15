using Panelith.App.Services;

namespace Panelith.App.Tests;

public sealed class MediaFoldersTests
{
    [Theory]
    [InlineData("Leher (Official Video) | Shahid: Kriti", "Leher (Official Video) Shahid Kriti")]
    [InlineData("a/b\\c*?\"<>d", "a b c d")]
    [InlineData("  ends with dots...  ", "ends with dots")]
    [InlineData("tab\tand\nnewline", "tab and newline")]
    [InlineData("", "video")]
    [InlineData(null, "video")]
    [InlineData("???", "video")]
    public void NamesBecomeSafeFileNames(string? name, string expected) =>
        Assert.Equal(expected, MediaFolders.SafeName(name));

    [Fact]
    public void LongNamesAreShortened() =>
        Assert.Equal(100, MediaFolders.SafeName(new string('x', 300)).Length);

    [Fact]
    public void FreshFilesNeverReplaceOldOnes()
    {
        var folder = Path.Combine(Path.GetTempPath(), "panelith-folders-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = MediaFolders.FreshFile(folder, "Clip", ".mp4");
            File.WriteAllText(first, "x");
            var second = MediaFolders.FreshFile(folder, "Clip", ".mp4");

            Assert.Equal(Path.Combine(folder, "Clip.mp4"), first);
            Assert.Equal(Path.Combine(folder, "Clip (2).mp4"), second);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
