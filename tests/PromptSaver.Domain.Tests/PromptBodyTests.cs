using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Tests;

public sealed class PromptBodyTests
{
    [Fact]
    public void CreatePreservesExactText()
    {
        const string text = "  Write a plan\r\nwith café and 漢字.  ";

        PromptBody body = PromptBody.Create(text);

        Assert.Equal(text, body.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void CreateRejectsWhitespaceOnlyText(string text)
    {
        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(() => PromptBody.Create(text));

        Assert.Equal("prompt.body.required", exception.Code);
    }

    [Fact]
    public void CreateAcceptsExactlyMaximumUtf8Size()
    {
        string text = new('a', PromptBody.MaximumUtf8Bytes);

        PromptBody body = PromptBody.Create(text);

        Assert.Equal(PromptBody.MaximumUtf8Bytes, body.Utf8ByteCount);
    }

    [Fact]
    public void CreateRejectsTextAboveMaximumUtf8Size()
    {
        string text = string.Concat(
            new string('a', PromptBody.MaximumUtf8Bytes - 2),
            "€");

        DomainValidationException exception =
            Assert.Throws<DomainValidationException>(() => PromptBody.Create(text));

        Assert.Equal("prompt.body.too_large", exception.Code);
    }
}
