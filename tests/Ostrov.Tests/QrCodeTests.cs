using Ostrov.Modules.Phone.Bridge;
using Xunit;

namespace Ostrov.Tests;

public class QrCodeTests
{
    [Fact]
    public void Encode_ShortUrl_ProducesSquareMatrixWithFinders()
    {
        var matrix = QrCode.Encode("http://192.168.1.5:54321/Abc123/");
        Assert.Equal(matrix.GetLength(0), matrix.GetLength(1));
        AssertFinderPattern(matrix, 0, 0);
        AssertFinderPattern(matrix, 0, matrix.GetLength(1) - 7);
        AssertFinderPattern(matrix, matrix.GetLength(0) - 7, 0);
    }

    [Fact]
    public void Encode_DifferentData_ProducesDifferentMatrix()
    {
        var a = QrCode.Encode("http://192.168.1.5:54321/AAAA/");
        var b = QrCode.Encode("http://192.168.1.5:54321/BBBB/");
        Assert.False(MatrixEquals(a, b));
    }

    [Fact]
    public void Encode_LongPayload_UsesBiggerVersion()
    {
        var shortMatrix = QrCode.Encode("hello");
        var longMatrix = QrCode.Encode(new string('x', 150));
        Assert.True(longMatrix.GetLength(0) > shortMatrix.GetLength(0));
    }

    [Fact]
    public void Encode_TooLongPayload_Throws()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 5000)));
    }

    [Theory]
    [InlineData(1, 14)]
    [InlineData(4, 62)]
    [InlineData(10, 213)]
    public void Capacity_KnowsVersionLimits(int version, int expected)
    {
        Assert.Equal(expected, QrCode.Capacity(version));
    }

    private static void AssertFinderPattern(bool[,] matrix, int top, int left)
    {
        for (var y = 0; y < 7; y++)
        for (var x = 0; x < 7; x++)
        {
            var border = y is 0 or 6 || x is 0 or 6;
            var center = y is >= 2 and <= 4 && x is >= 2 and <= 4;
            Assert.Equal(border || center, matrix[top + y, left + x]);
        }
    }

    private static bool MatrixEquals(bool[,] a, bool[,] b)
    {
        if (a.GetLength(0) != b.GetLength(0) || a.GetLength(1) != b.GetLength(1)) return false;
        for (var y = 0; y < a.GetLength(0); y++)
        for (var x = 0; x < a.GetLength(1); x++)
            if (a[y, x] != b[y, x]) return false;
        return true;
    }
}
