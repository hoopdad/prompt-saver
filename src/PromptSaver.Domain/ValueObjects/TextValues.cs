using System.Globalization;
using System.Text;

namespace PromptSaver.Domain.ValueObjects;

public sealed record PromptTitle
{
    public const int MaximumCharacters = 200;

    private PromptTitle(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static PromptTitle? Create(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(
                "prompt.title.required",
                "A supplied title cannot be whitespace.",
                nameof(value));
        }

        if (value.Length > MaximumCharacters)
        {
            throw new DomainValidationException(
                "prompt.title.too_long",
                $"Prompt title cannot exceed {MaximumCharacters} characters.",
                nameof(value));
        }

        return new PromptTitle(value);
    }

    public override string ToString() => Value;
}

public readonly record struct ScoreBasisPoints
{
    public const int Minimum = 0;
    public const int Maximum = 10_000;

    private ScoreBasisPoints(int value)
    {
        Value = value;
    }

    public int Value { get; }

    public static ScoreBasisPoints Create(int value)
    {
        if (value is < Minimum or > Maximum)
        {
            throw new DomainValidationException(
                "score.out_of_range",
                $"Score must be between {Minimum} and {Maximum} basis points.",
                nameof(value));
        }

        return new ScoreBasisPoints(value);
    }

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public readonly record struct Confidence
{
    private Confidence(decimal value)
    {
        Value = value;
    }

    public decimal Value { get; }

    public static Confidence Create(decimal value)
    {
        if (value is < 0 or > 1)
        {
            throw new DomainValidationException(
                "confidence.out_of_range",
                "Confidence must be between 0 and 1.",
                nameof(value));
        }

        return new Confidence(value);
    }
}

public static class NormalizedText
{
    public static string CreateKey(string value, string code, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(code, "Value cannot be empty or whitespace.", parameterName);
        }

        string normalized = value
            .Normalize(NormalizationForm.FormKC)
            .Replace('-', ' ')
            .Trim()
            .TrimEnd('.', '!', '?', ';', ':');
        return string.Join(
            ' ',
            normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}
