using System.Text.Json;
using ModelContextProtocol;
using Shouldly;

namespace Collabot.Collattice.Api.Tests;

// Allowlist-policy unit tests for the McpErrorTranslator. End-to-end
// wiring (CallToolFilter → McpException → SDK wrapper) is exercised by
// McpServerErrorWrapperTests; this file owns the allowlist decision matrix.
public class McpErrorTranslatorTests
{
    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(ArgumentNullException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(OverflowException))]
    [InlineData(typeof(NotSupportedException))]
    public void ShouldSurface_OperatorInputShapes_ReturnsTrue(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "test message")!;

        McpErrorTranslator.ShouldSurface(ex).ShouldBeTrue();
    }

    [Fact]
    public void ShouldSurface_JsonException_ReturnsTrue()
    {
        // JsonException requires a different ctor than the rest; test it explicitly.
        var ex = new JsonException("malformed");

        McpErrorTranslator.ShouldSurface(ex).ShouldBeTrue();
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NullReferenceException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void ShouldSurface_ServerInternalShapes_ReturnsFalse(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, "internal detail")!;

        McpErrorTranslator.ShouldSurface(ex).ShouldBeFalse();
    }

    [Fact]
    public void Format_IncludesTypeNameAndMessage()
    {
        var ex = new ArgumentException("The arguments dictionary is missing a value for the required parameter 'content'.", "arguments");

        var formatted = McpErrorTranslator.Format(ex);

        formatted.ShouldStartWith("ArgumentException");
        formatted.ShouldContain("missing a value for the required parameter");
        formatted.ShouldContain("'content'");
    }

    [Fact]
    public void Format_EmptyMessage_StillReturnsTypeName()
    {
        // A constructed-but-empty-message exception still produces a useful surface.
        var ex = new ArgumentException(string.Empty);

        var formatted = McpErrorTranslator.Format(ex);

        formatted.ShouldBe("ArgumentException");
    }

    [Fact]
    public void Format_NeverIncludesStackTrace()
    {
        // Guard against accidental ToString() reach for internal detail.
        Exception caught;
        try
        {
            throw new ArgumentException("boom");
        }
        catch (ArgumentException ex)
        {
            caught = ex;
        }

        var formatted = McpErrorTranslator.Format(caught);

        formatted.ShouldNotContain(" at ");          // no stack frames
        formatted.ShouldNotContain(nameof(McpErrorTranslatorTests));
    }

    [Fact]
    public void ToMcpException_WrapsOriginalAsInner()
    {
        var original = new ArgumentException("bad input");

        var wrapped = McpErrorTranslator.ToMcpException(original);

        wrapped.ShouldBeOfType<McpException>();
        wrapped.InnerException.ShouldBeSameAs(original);
        wrapped.Message.ShouldStartWith("ArgumentException");
    }
}
