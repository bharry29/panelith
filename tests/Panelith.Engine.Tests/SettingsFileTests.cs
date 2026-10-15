using System.Text.Json;
using Panelith.Engine.Configuration;

namespace Panelith.Engine;

public sealed class SettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "panelith-settings-" + Guid.NewGuid().ToString("N"));

    public SettingsFileTests() => Directory.CreateDirectory(_folder);

    private sealed record Sample(string Name = "", int Count = 0);

    [Fact]
    public void AMissingFileIsNotAnError()
    {
        Assert.Null(SettingsFile.Read<Sample>(Path.Combine(_folder, "none.json"), JsonSerializerOptions.Default, out var quarantined));
        Assert.Null(quarantined);
    }

    [Fact]
    public void AGoodFileLoads()
    {
        var file = Path.Combine(_folder, "good.json");
        File.WriteAllText(file, """{ "Name": "x", "Count": 3 }""");
        Assert.Equal(new Sample("x", 3), SettingsFile.Read<Sample>(file, JsonSerializerOptions.Default, out _));
    }

    [Fact]
    public void ADamagedFileIsMovedAsideNotLost()
    {
        var file = Path.Combine(_folder, "app.json");
        File.WriteAllText(file, "{ this is not json");

        Assert.Null(SettingsFile.Read<Sample>(file, JsonSerializerOptions.Default, out var quarantined));
        Assert.Equal(file + ".bad", quarantined);
        Assert.False(File.Exists(file));
        Assert.Equal("{ this is not json", File.ReadAllText(file + ".bad"));
    }

    [Fact]
    public void AnOversizedFileIsMovedAsideUnread()
    {
        var file = Path.Combine(_folder, "huge.json");
        using (var stream = File.Create(file))
        {
            stream.SetLength(SettingsFile.MaxBytes + 1);
        }

        Assert.Null(SettingsFile.Read<Sample>(file, JsonSerializerOptions.Default, out var quarantined));
        Assert.NotNull(quarantined);
    }

    [Fact]
    public void ClipShortensLongText()
    {
        Assert.Equal("abc", SettingsFile.Clip("abcdef", 3));
        Assert.Equal("ab", SettingsFile.Clip("ab", 3));
        Assert.Equal(string.Empty, SettingsFile.Clip(null, 3));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
