using Xunit;

namespace Panelith.Rendering;

public sealed class FontResolverTests
{
    [Fact]
    public void Resolve_UnknownFamily_ReturnsUsableTypeface()
    {
        using var resolver = new FontResolver();
        var face = resolver.Resolve("NoSuchFontFamily_Panelith_Test", 700, italic: true);
        Assert.NotNull(face);
        Assert.NotEqual(IntPtr.Zero, face.Handle);
    }

    [Fact]
    public void Resolve_CachesSameKey()
    {
        using var resolver = new FontResolver();
        var a = resolver.Resolve("Inter", 400, false);
        var b = resolver.Resolve("Inter", 400, false);
        Assert.Same(a, b);
    }
}
