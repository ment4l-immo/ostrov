using Ostrov.Search;
using Xunit;

namespace Ostrov.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2*3", 8d)]
    [InlineData("2(3+4)", 14d)]
    [InlineData("2π", 6.283185307179586d)]
    [InlineData("2^3^2", 512d)]
    [InlineData("-2^2", -4d)]
    [InlineData("(-2)^2", 4d)]
    [InlineData("1,5+2.5", 4d)]
    [InlineData("10/4", 2.5d)]
    [InlineData("2x3", 6d)]
    [InlineData("2·3", 6d)]
    public void TryEvaluate_Arithmetic(string expression, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expression, out var value), expression);
        Assert.Equal(expected, value, 8);
    }

    [Theory]
    [InlineData("15% от 200", 30d)]
    [InlineData("15% of 200", 30d)]
    [InlineData("200+15%", 230d)]
    [InlineData("200-15%", 170d)]
    [InlineData("200*15%", 30d)]
    [InlineData("200/15%", 1333.3333333333333d)]
    public void TryEvaluate_Percents(string expression, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expression, out var value), expression);
        Assert.Equal(expected, value, 6);
    }

    [Theory]
    [InlineData("sqrt(9)", 3d)]
    [InlineData("корень(16)", 4d)]
    [InlineData("√25", 5d)]
    [InlineData("abs(-3)", 3d)]
    [InlineData("ln(1)", 0d)]
    [InlineData("log(1000)", 3d)]
    [InlineData("pi", 3.141592653589793d)]
    public void TryEvaluate_Functions(string expression, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expression, out var value), expression);
        Assert.Equal(expected, value, 8);
    }

    [Fact]
    public void TryEvaluate_TrigonometryUsesConfiguredDegrees()
    {
        var old = Calculator.Degrees;
        try
        {
            Calculator.Degrees = true;
            Assert.True(Calculator.TryEvaluate("sin(30)", out var sin));
            Assert.Equal(0.5d, sin, 8);
            Assert.True(Calculator.TryEvaluate("cos(90)", out var cos));
            Assert.Equal(0d, cos, 8);

            Calculator.Degrees = false;
            Assert.True(Calculator.TryEvaluate("sin(pi/6)", out sin));
            Assert.Equal(0.5d, sin, 8);
        }
        finally
        {
            Calculator.Degrees = old;
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("42")]
    [InlineData("-5")]
    [InlineData("2+")]
    [InlineData("1/0")]
    [InlineData("sqrt(-1)")]
    [InlineData("tan(90)")]
    [InlineData("привет")]
    public void TryEvaluate_RejectsNonExpressions(string expression)
    {
        Assert.False(Calculator.TryEvaluate(expression, out _), expression);
    }

    [Fact]
    public void TryEvaluate_RejectsTooLongExpression()
    {
        Assert.False(Calculator.TryEvaluate(new string('1', 201), out _));
    }

    [Theory]
    [InlineData(8d, "8")]
    [InlineData(2.5d, "2,5")]
    [InlineData(12345.5, "12345,5")]
    [InlineData(-0.0000001, "-1E-07")]
    public void FormatPlain_FormatsDecimalComma(double value, string expected)
    {
        Assert.Equal(expected, Calculator.FormatPlain(value));
    }

    [Fact]
    public void FormatDisplay_GroupsLargeNumbers()
    {
        Assert.Equal("12 345,5", Calculator.FormatDisplay(12345.5));
        Assert.Equal("8", Calculator.FormatDisplay(8));
    }
}
