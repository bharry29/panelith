using Panelith.App.Services;

namespace Panelith.App.Tests;

public sealed class YouTubeUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtube.com/watch?v=jNQXAC9IVRw&list=PL123&index=4&t=30s")]
    [InlineData("http://m.youtube.com/watch?feature=share&v=jNQXAC9IVRw")]
    [InlineData("youtu.be/jNQXAC9IVRw?si=abc")]
    [InlineData("https://www.youtube.com/shorts/jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/live/jNQXAC9IVRw?feature=x")]
    [InlineData("  https://music.youtube.com/watch?v=jNQXAC9IVRw  ")]
    public void SingleVideoLinksBecomeAPlainWatchAddress(string text) =>
        Assert.Equal("https://www.youtube.com/watch?v=jNQXAC9IVRw", YouTubeDownloader.ParseVideoUrl(text)?.ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jNQXAC9IVRw")]
    [InlineData("https://www.youtube.com/playlist?list=PL123")]
    [InlineData("https://www.youtube.com/@channel")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=jNQXAC9IVRw;calc")]
    [InlineData("https://evil.example/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtube.com.evil.example/watch?v=jNQXAC9IVRw")]
    [InlineData("https://user@youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("https://youtube.com:8443/watch?v=jNQXAC9IVRw")]
    [InlineData("file://youtube.com/watch?v=jNQXAC9IVRw")]
    [InlineData("--exec calc https://youtu.be/jNQXAC9IVRw")]
    [InlineData("https://youtu.be/--exec=calc")]
    public void AnythingElseIsRefused(string? text) => Assert.Null(YouTubeDownloader.ParseVideoUrl(text));
}
