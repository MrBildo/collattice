using Shouldly;

namespace Collabot.Collattice.Api.Tests;

public class SemVerTests
{
    [Theory]
    [InlineData("1.16.0", 1, 16, 0)]
    [InlineData("v1.16.0", 1, 16, 0)]
    [InlineData("V2.0.5", 2, 0, 5)]
    [InlineData("1.16.0+build.42", 1, 16, 0)]
    [InlineData("1.16.0-rc.1", 1, 16, 0)]
    [InlineData("1.16", 1, 16, 0)]
    [InlineData("1", 1, 0, 0)]
    [InlineData("  1.16.0  ", 1, 16, 0)]
    public void TryParse_ValidInput_ParsesNumericCore(string input, int major, int minor, int patch)
    {
        var parsed = SemVer.TryParse(input, out var result);

        parsed.ShouldBeTrue();
        result.ShouldBe(new SemVer(major, minor, patch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("v")]
    public void TryParse_InvalidInput_ReturnsFalse(string? input)
    {
        var parsed = SemVer.TryParse(input, out var result);

        parsed.ShouldBeFalse();
        result.ShouldBe(default);
    }

    [Fact]
    public void Compare_GreaterMajor_IsGreater() =>
        (new SemVer(2, 0, 0) > new SemVer(1, 9, 9)).ShouldBeTrue();

    [Fact]
    public void Compare_GreaterMinor_IsGreater() =>
        (new SemVer(1, 16, 0) > new SemVer(1, 9, 0)).ShouldBeTrue();

    [Fact]
    public void Compare_GreaterPatch_IsGreater() =>
        (new SemVer(1, 16, 2) > new SemVer(1, 16, 1)).ShouldBeTrue();

    [Fact]
    public void Compare_Equal_IsNotGreater()
    {
        var a = new SemVer(1, 16, 0);
        var b = new SemVer(1, 16, 0);

        (a > b).ShouldBeFalse();
        (a < b).ShouldBeFalse();
        a.ShouldBe(b);
    }

    [Fact]
    public void DevSentinel_IsZeroZeroZero()
    {
        SemVer.DevSentinel.ShouldBe(new SemVer(0, 0, 0));
        new SemVer(0, 0, 0).IsDevSentinel.ShouldBeTrue();
        new SemVer(1, 0, 0).IsDevSentinel.ShouldBeFalse();
    }

    [Fact]
    public void ToString_RendersNumericCore() =>
        new SemVer(1, 16, 3).ToString().ShouldBe("1.16.3");
}
