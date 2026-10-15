using Panelith.App.Services;
using Panelith.Engine;

namespace Panelith.App.Tests;

public sealed class AppSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panelith-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AFirstLaunchHasNothingToRestore()
    {
        var session = new AppSessionStore(new AppPaths(_root)).Load();

        Assert.Equal(new AppSession(), session);
    }

    [Fact]
    public void TheSessionComesBackAsItWasLeft()
    {
        var store = new AppSessionStore(new AppPaths(_root));
        var left = new AppSession
        {
            Window = new WindowPlacement(-1700, 120, 1400.5, 900, Maximized: true),
            Page = "Designer",
            SensorFilter = "gpu",
            DesignerScreen = "my-lake-video",
            SelectedProfile = "gaming",
        };

        store.Save(left);

        Assert.Equal(left, new AppSessionStore(new AppPaths(_root)).Load());
    }

    [Fact]
    public void ADamagedFileIsSetAsideAndTheWindowOpensAsNew()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var store = new AppSessionStore(paths);
        File.WriteAllText(store.File, "{ not json");

        Assert.Equal(new AppSession(), store.Load());
        Assert.Equal(store.File + ".bad", store.LastQuarantined);
    }

    [Fact]
    public void ImpossibleWindowSizesAreDropped()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var store = new AppSessionStore(paths);
        File.WriteAllText(store.File, """{ "window": { "x": 0, "y": 0, "width": 0, "height": 1e9, "maximized": false }, "page": "Sensors" }""");

        var session = store.Load();

        Assert.Null(session.Window);
        Assert.Equal("Sensors", session.Page);
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
