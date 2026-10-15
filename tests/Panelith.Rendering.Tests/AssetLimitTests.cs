using Panelith.Imaging;

namespace Panelith.Rendering;

public sealed class AssetLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "panelith-assets-" + Guid.NewGuid().ToString("N"));
    private readonly string _theme;
    private readonly string _sibling;

    public AssetLimitTests()
    {
        _theme = Path.Combine(_root, "rig");
        _sibling = Path.Combine(_root, "rig-other");
        Directory.CreateDirectory(_theme);
        Directory.CreateDirectory(_sibling);

        var codec = new SkiaImageCodec();
        using var frame = FrameBuffer.RentCleared(10, 10);
        var png = codec.EncodePng(frame);
        File.WriteAllBytes(Path.Combine(_theme, "ok.png"), png);
        File.WriteAllBytes(Path.Combine(_sibling, "secret.png"), png);
    }

    [Fact]
    public void ImagesInsideTheThemeLoad()
    {
        using var cache = new ThemeAssetCache(_theme);
        Assert.Equal(10, cache.GetBitmap("ok.png").Width);
    }

    [Theory]
    [InlineData(@"..\rig-other\secret.png")]
    [InlineData("../rig-other/secret.png")]
    [InlineData(@"sub\..\..\rig-other\secret.png")]
    [InlineData("ok.png:stream")]
    public void PathsLeavingTheThemeFolderAreRefused(string path)
    {
        using var cache = new ThemeAssetCache(_theme);
        Assert.Null(cache.TryReadBytes(path));
        Assert.NotEqual(10, cache.GetBitmap(path).Width);
    }

    [Fact]
    public void OversizedImagesAreRefused()
    {
        var big = Path.Combine(_theme, "big.png");
        using (var stream = File.Create(big))
        {
            stream.SetLength(ThemeAssetCache.MaxAssetBytes + 1);
        }

        using var cache = new ThemeAssetCache(_theme);
        Assert.Null(cache.TryReadBytes("big.png"));
    }

    [Fact]
    public void HugeImageDimensionsAreRefused()
    {
        SkiaImageCodec.CheckDimensions(SkiaImageCodec.MaxImageDimension, SkiaImageCodec.MaxImageDimension);
        Assert.Throws<InvalidOperationException>(() => SkiaImageCodec.CheckDimensions(SkiaImageCodec.MaxImageDimension + 1, 10));
        Assert.Throws<InvalidOperationException>(() => new SkiaImageCodec().Decode(new SkiaImageCodec().EncodePng(FrameBuffer.RentCleared(4, 4)), 8000, 8000));
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
