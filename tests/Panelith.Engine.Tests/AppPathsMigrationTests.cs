namespace Panelith.Engine;

public sealed class AppPathsMigrationTests : IDisposable
{
    private readonly string _parent = Path.Combine(Path.GetTempPath(), "panelith-migrate-" + Guid.NewGuid().ToString("N"));

    public AppPathsMigrationTests() => Directory.CreateDirectory(_parent);

    [Fact]
    public void TheOldFolderMovesAcrossWithItsConfiguration()
    {
        var legacy = Path.Combine(_parent, "RigGlass");
        Directory.CreateDirectory(Path.Combine(legacy, "themes", "mine"));
        File.WriteAllText(Path.Combine(legacy, "rigglass.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "app.json"), "{}");
        var root = Path.Combine(_parent, "Panelith");

        Assert.Equal(legacy, AppPaths.MigrateLegacy(root));

        Assert.False(Directory.Exists(legacy));
        Assert.True(Directory.Exists(Path.Combine(root, "themes", "mine")));
        Assert.True(File.Exists(Path.Combine(root, "panelith.json")));
        Assert.True(File.Exists(Path.Combine(root, "app.json")));
    }

    [Fact]
    public void AnExistingNewFolderIsNeverOverwritten()
    {
        var legacy = Path.Combine(_parent, "RigGlass");
        Directory.CreateDirectory(legacy);
        var root = Path.Combine(_parent, "Panelith");
        Directory.CreateDirectory(root);

        Assert.Null(AppPaths.MigrateLegacy(root));
        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void ACustomRootDoesNotAdoptASiblingFolder()
    {
        Directory.CreateDirectory(Path.Combine(_parent, "RigGlass"));

        Assert.Null(AppPaths.MigrateLegacy(Path.Combine(_parent, "my-config")));
        Assert.True(Directory.Exists(Path.Combine(_parent, "RigGlass")));
    }

    [Fact]
    public void NothingHappensWithoutAnOldFolder() =>
        Assert.Null(AppPaths.MigrateLegacy(Path.Combine(_parent, "Panelith")));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_parent, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
