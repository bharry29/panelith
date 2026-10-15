using OmniPanel.Expressions;
using OmniPanel.Sensors;
using Xunit;

namespace OmniPanel.Tests.Expressions;

public sealed class BindingExpressionTests
{
    [Theory]
    [InlineData("1 + 2", 3d)]
    [InlineData("2 * 3 + 1", 7d)]
    [InlineData("1 + 2 * 3", 7d)]
    [InlineData("(1 + 2) * 3", 9d)]
    [InlineData("10 / 4", 2.5d)]
    [InlineData("10 % 3", 1d)]
    [InlineData("-5 + 10", 5d)]
    [InlineData("2 - -3", 5d)]
    public void EvaluatesArithmeticWithCorrectPrecedence(string expression, double expected)
    {
        var result = BindingExpression.Parse(expression).Evaluate(EmptyBindingContext.Instance);

        Assert.Equal(expected, result.AsNumber());
    }

    [Theory]
    [InlineData("1 < 2", true)]
    [InlineData("2 <= 2", true)]
    [InlineData("3 > 4", false)]
    [InlineData("1 == 1", true)]
    [InlineData("1 != 1", false)]
    [InlineData("1 < 2 && 3 < 4", true)]
    [InlineData("1 > 2 || 3 < 4", true)]
    [InlineData("!(1 > 2)", true)]
    public void EvaluatesComparisonsAndLogic(string expression, bool expected)
    {
        var result = BindingExpression.Parse(expression).Evaluate(EmptyBindingContext.Instance);

        Assert.Equal(expected, result.AsBoolean());
    }

    [Theory]
    [InlineData("1 > 2 ? 10 : 20", 20d)]
    [InlineData("1 < 2 ? 10 : 20", 10d)]
    [InlineData("1 < 2 ? 2 > 3 ? 1 : 2 : 3", 2d)]
    public void EvaluatesNestedConditionals(string expression, double expected)
    {
        var result = BindingExpression.Parse(expression).Evaluate(EmptyBindingContext.Instance);

        Assert.Equal(expected, result.AsNumber());
    }

    [Fact]
    public void SuggestsTheAliasFormWhenTheAtSignIsMissing()
    {
        BindingExpression.TryParse("cpu.temp > 80", out _, out var error);

        Assert.Contains("Did you mean @cpu.temp?", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolvesSensorAliases()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 62.5d, SensorKind.Temperature);

        var result = BindingExpression.Parse("@cpu.temp").Evaluate(context);

        Assert.Equal(62.5d, result.AsNumber());
    }

    [Fact]
    public void ResolvesBracketedRawSensorIds()
    {
        var context = new TestBindingContext().WithNumber("cpu/0/temperature/package", 71d);

        var result = BindingExpression.Parse("[cpu/0/temperature/package]").Evaluate(context);

        Assert.Equal(71d, result.AsNumber());
    }

    [Fact]
    public void DistinguishesDivisionFromRawSensorIds()
    {
        var context = new TestBindingContext()
            .WithNumber("@mem.used", 8d * 1024 * 1024 * 1024)
            .WithNumber("@mem.total", 32d * 1024 * 1024 * 1024);

        var result = BindingExpression.Parse("@mem.used / @mem.total * 100").Evaluate(context);

        Assert.Equal(25d, result.AsNumber());
    }

    [Fact]
    public void MissingSensorYieldsMissingRatherThanZero()
    {
        var result = BindingExpression.Parse("@gpu.temp").Evaluate(new TestBindingContext());

        Assert.False(result.HasValue);
        Assert.Equal(BindingValueKind.Missing, result.Kind);
    }

    [Fact]
    public void ArithmeticOnMissingSensorStaysMissing()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 50d);

        var result = BindingExpression.Parse("@cpu.temp + @gpu.temp").Evaluate(context);

        Assert.False(result.HasValue);
    }

    [Fact]
    public void DivisionByZeroYieldsMissingRatherThanInfinity()
    {
        var result = BindingExpression.Parse("100 / 0").Evaluate(EmptyBindingContext.Instance);

        Assert.False(result.HasValue);
    }

    [Fact]
    public void AggregatesSkipMissingInputs()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 55d);

        var result = BindingExpression.Parse("max(@cpu.temp, @gpu.temp)").Evaluate(context);

        Assert.Equal(55d, result.AsNumber());
    }

    [Fact]
    public void AggregateOfNothingIsMissing()
    {
        var result = BindingExpression.Parse("max(@cpu.temp, @gpu.temp)").Evaluate(new TestBindingContext());

        Assert.False(result.HasValue);
    }

    [Theory]
    [InlineData("round(2.5)", 3d)]
    [InlineData("round(2.345, 2)", 2.35d)]
    [InlineData("clamp(150, 0, 100)", 100d)]
    [InlineData("abs(-7)", 7d)]
    [InlineData("map(1250, 300, 2200, 0, 100)", 50d)]
    [InlineData("avg(10, 20, 30)", 20d)]
    [InlineData("pow(2, 10)", 1024d)]
    public void EvaluatesBuiltInFunctions(string expression, double expected)
    {
        var result = BindingExpression.Parse(expression).Evaluate(EmptyBindingContext.Instance);

        Assert.Equal(expected, result.AsNumber()!.Value, precision: 6);
    }

    [Fact]
    public void MapClampsToTheOutputRange()
    {
        var result = BindingExpression.Parse("map(3000, 300, 2200, 0, 100)").Evaluate(EmptyBindingContext.Instance);

        Assert.Equal(100d, result.AsNumber());
    }

    [Fact]
    public void CoalescePicksTheFirstAvailableValue()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 44d);

        var result = BindingExpression.Parse("coalesce(@gpu.temp, @cpu.temp, 0)").Evaluate(context);

        Assert.Equal(44d, result.AsNumber());
    }

    [Fact]
    public void IfDoesNotEvaluateTheUntakenBranch()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 90d);

        // The false branch divides by zero; taking it would produce Missing.
        var result = BindingExpression.Parse("if(@cpu.temp > 80, 1, 1 / 0)").Evaluate(context);

        Assert.Equal(1d, result.AsNumber());
    }

    [Fact]
    public void HasReportsSensorAvailability()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 40d);

        Assert.True(BindingExpression.Parse("has(@cpu.temp)").Evaluate(context).AsBoolean());
        Assert.False(BindingExpression.Parse("has(@gpu.temp)").Evaluate(context).AsBoolean());
    }

    [Fact]
    public void WindowedAggregatesReadHistory()
    {
        var context = new TestBindingContext()
            .WithNumber("@cpu.temp", 50d)
            .WithHistory("@cpu.temp", 40d, 60d, 80d);

        Assert.Equal(60d, BindingExpression.Parse("avgover(@cpu.temp, 60)").Evaluate(context).AsNumber());
        Assert.Equal(80d, BindingExpression.Parse("maxover(@cpu.temp, 60)").Evaluate(context).AsNumber());
        Assert.Equal(40d, BindingExpression.Parse("minover(@cpu.temp, 60)").Evaluate(context).AsNumber());
    }

    [Fact]
    public void WindowedAggregateFallsBackToTheCurrentValueWithoutHistory()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 50d);

        var result = BindingExpression.Parse("avgover(@cpu.temp, 60)").Evaluate(context);

        Assert.Equal(50d, result.AsNumber());
    }

    [Fact]
    public void ResolvesThemeVariables()
    {
        var context = new TestBindingContext()
            .WithNumber("@mem.used", 4d)
            .WithNumber("@mem.total", 16d)
            .WithVariable("memPercent", "@mem.used / @mem.total * 100");

        var result = BindingExpression.Parse("$memPercent").Evaluate(context);

        Assert.Equal(25d, result.AsNumber());
    }

    [Fact]
    public void AcceptsUnitSuffixesOnNumericLiterals()
    {
        var context = new TestBindingContext().WithNumber("@cpu.temp", 85d);

        var result = BindingExpression.Parse("@cpu.temp > 80c").Evaluate(context);

        Assert.True(result.AsBoolean());
    }

    [Fact]
    public void ConcatenatesWhenEitherSideIsText()
    {
        var context = new TestBindingContext().WithText("@cpu.name", "Ryzen 9 9950X");

        var result = BindingExpression.Parse("@cpu.name + \" @ \" + 5.7").Evaluate(context);

        Assert.Equal("Ryzen 9 9950X @ 5.7", result.Text);
    }

    [Fact]
    public void ReportsReferencedSensorsForChangeTracking()
    {
        var expression = BindingExpression.Parse("max(@cpu.temp, @gpu.temp) + [disk/0/temperature]");

        Assert.Equal(
            ["@cpu.temp", "@gpu.temp", "disk/0/temperature"],
            expression.ReferencedSensors.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("1 +", "ended unexpectedly")]
    [InlineData("(1 + 2", "Expected ')'")]
    [InlineData("nosuchfunc(1)", "Unknown function")]
    [InlineData("cpu.temp", "not a value")]
    [InlineData("@", "Expected a sensor alias")]
    [InlineData("[unterminated", "Unterminated sensor reference")]
    public void ReportsParseErrorsWithUsefulMessages(string expression, string expectedFragment)
    {
        var parsed = BindingExpression.TryParse(expression, out _, out var error);

        Assert.False(parsed);
        Assert.Contains(expectedFragment, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParsingIsCachedSoRepeatedUseIsFree()
    {
        var first = BindingExpression.Parse("@cpu.temp * 2");
        var second = BindingExpression.Parse("@cpu.temp * 2");

        Assert.Same(first, second);
    }
}
