using System.Text;

namespace PromptSaver.Domain.ValueObjects;

public sealed record PromptBody
{
    public const int MaximumUtf8Bytes = 256 * 1024;

    private PromptBody(string value, int utf8ByteCount)
    {
        Value = value;
        Utf8ByteCount = utf8ByteCount;
    }

    public string Value { get; }

    public int Utf8ByteCount { get; }

    public static PromptBody Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                "prompt.body.required",
                "Prompt body cannot be empty or whitespace.",
                nameof(value));
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaximumUtf8Bytes)
        {
            throw new DomainValidationException(
                "prompt.body.too_large",
                $"Prompt body cannot exceed {MaximumUtf8Bytes} UTF-8 bytes.",
                nameof(value));
        }

        return new PromptBody(value, byteCount);
    }

    public override string ToString() => Value;
}
