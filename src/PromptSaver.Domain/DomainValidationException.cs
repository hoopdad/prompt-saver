namespace PromptSaver.Domain;

public sealed class DomainValidationException : ArgumentException
{
    public DomainValidationException(string code, string message, string? paramName = null)
        : base(message, paramName)
    {
        Code = code;
    }

    public string Code { get; }
}
