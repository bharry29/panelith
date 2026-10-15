using System.Text;
using Panelith.Expressions;
using Panelith.Themes;

namespace Panelith;

public sealed class InputLimitTests
{
    [Fact]
    public void OverlongExpressionsAreRefused()
    {
        var text = string.Join("+", Enumerable.Repeat("1", 3000));
        Assert.False(BindingExpression.TryParse(text, out _, out var error));
        Assert.Contains("longer than", error);
        Assert.False(TextTemplate.TryParse("{" + text + "}", out _, out _));
    }

    [Fact]
    public void DeeplyNestedExpressionsAreRefusedWithoutOverflowingTheStack()
    {
        var parens = new string('(', 1000) + "1" + new string(')', 1000);
        Assert.False(BindingExpression.TryParse(parens, out _, out var error));
        Assert.Contains("nests", error);

        var negations = new string('-', 1000) + "1";
        Assert.False(BindingExpression.TryParse(negations, out _, out _));
    }

    [Fact]
    public void LongOperatorChainsAreRefusedBecauseEvaluatingThemRecurses()
    {
        var chain = string.Join("+", Enumerable.Repeat("1", 200));
        Assert.False(BindingExpression.TryParse(chain, out _, out var error));
        Assert.Contains("nests", error);
    }

    [Fact]
    public void OrdinaryNestingStillParses()
    {
        var nested = new string('(', 30) + "@cpu.load * 2" + new string(')', 30);
        Assert.True(BindingExpression.TryParse(nested, out var expression, out _));
        Assert.Equal(["@cpu.load"], expression!.ReferencedSensors);

        var chain = string.Join(" + ", Enumerable.Repeat("1", 40));
        Assert.True(BindingExpression.TryParse(chain, out _, out _));
    }

    [Fact]
    public void ThemesWithTooManyWidgetsAreRefused()
    {
        var widgets = string.Join(",", Enumerable.Repeat("""{ "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "a" }""", ThemeSerializer.MaxWidgets + 1));
        var ex = Assert.Throws<ThemeFormatException>(() => ThemeSerializer.Deserialize(Document(widgets)));
        Assert.Contains("widgets", ex.Message);
    }

    [Fact]
    public void ThemesNestingGroupsTooDeeplyAreRefused()
    {
        Assert.Throws<ThemeFormatException>(() => ThemeSerializer.Deserialize(Document(Groups(ThemeSerializer.MaxGroupDepth + 2))));
        ThemeSerializer.Deserialize(Document(Groups(ThemeSerializer.MaxGroupDepth)));
    }

    [Fact]
    public void OversizedThemeFilesAreRefusedBeforeReading()
    {
        var file = Path.Combine(Path.GetTempPath(), "panelith-big-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            using (var stream = File.Create(file))
            {
                stream.SetLength(ThemeSerializer.MaxDocumentBytes + 1);
            }

            Assert.Throws<ThemeFormatException>(() => ThemeSerializer.ReadDocument(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Groups(int depth)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            builder.Append("""{ "type": "group", "x": 0, "y": 0, "width": 10, "height": 10, "items": [""");
        }

        builder.Append("""{ "type": "text", "x": 0, "y": 0, "width": 10, "height": 10, "text": "a" }""");
        for (var i = 0; i < depth; i++)
        {
            builder.Append("]}");
        }

        return builder.ToString();
    }

    private static string Document(string widgets) =>
        $$"""{ "schema": 1, "canvas": { "width": 100, "height": 100 }, "widgets": [{{widgets}}] }""";
}
