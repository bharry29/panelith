using Panelith.Engine;
using Panelith.Hosting.Profiles;
using Panelith.Imaging;

namespace Panelith.Hosting;

public sealed class SeamlessAndStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panelith-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void BlendMixesTwoFramesByAmount()
    {
        using var into = FrameBuffer.RentCleared(2, 1);
        using var from = FrameBuffer.RentCleared(2, 1);
        into.Span.Fill(200);
        from.Span.Fill(0);

        LiveThemeRendererFactory.Blend(into, from, 0.25);
        Assert.All(into.Span.ToArray(), b => Assert.InRange(b, 149, 151));

        LiveThemeRendererFactory.Blend(into, from, 0d);
        Assert.All(into.Span.ToArray(), b => Assert.InRange(b, 149, 151));

        LiveThemeRendererFactory.Blend(into, from, 1d);
        Assert.All(into.Span.ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BlendRefusesFramesOfDifferentSizes()
    {
        using var into = FrameBuffer.RentCleared(2, 1);
        using var from = FrameBuffer.RentCleared(1, 1);
        Assert.Throws<ArgumentException>(() => LiveThemeRendererFactory.Blend(into, from, 0.5));
    }

    [Fact]
    public void DimStepsTowardTheTargetWithoutOvershooting()
    {
        Assert.Equal(0.6, LiveThemeRendererFactory.StepDim(0.5, 1d, 0.1), 9);
        Assert.Equal(0.4, LiveThemeRendererFactory.StepDim(0.5, 0d, 0.1), 9);
        Assert.Equal(1d, LiveThemeRendererFactory.StepDim(0.95, 1d, 0.1), 9);
    }

    [Fact]
    public void ADamagedProfilesFileFallsBackToTheStarterSetAndIsKept()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var store = new ProfileStore(paths);
        File.WriteAllText(store.File, "[[[ not json");

        var set = store.Load();

        Assert.NotEmpty(set.Profiles);
        Assert.Equal(store.File + ".bad", store.LastQuarantined);
        Assert.True(File.Exists(store.File + ".bad"));
    }

    [Fact]
    public void HandEditedProfilesAreTrimmedToSensibleSizes()
    {
        var huge = new ProfileSet
        {
            Profiles = [.. Enumerable.Range(0, 100).Select(i => new Profile
            {
                Id = new string('x', 1000),
                Name = "P" + i,
                Dim = 5,
                RotationSeconds = -3,
                Screens = [.. Enumerable.Repeat("rig", 1000)],
            })],
        };

        var clean = ProfileStore.Sanitize(huge);

        Assert.Equal(ProfileStore.MaxProfiles, clean.Profiles.Count);
        Assert.All(clean.Profiles, p =>
        {
            Assert.Equal(ProfileStore.MaxTextLength, p.Id.Length);
            Assert.Equal(1d, p.Dim);
            Assert.Equal(1, p.RotationSeconds);
            Assert.Equal(ProfileStore.MaxListLength, p.Screens.Count);
        });
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
