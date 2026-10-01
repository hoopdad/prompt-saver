namespace PromptSaver.Infrastructure.Storage;

public sealed class PersistenceConcurrencyException : InvalidOperationException
{
    public PersistenceConcurrencyException(string message)
        : base(message)
    {
    }
}

public sealed class PersistenceIntegrityException : InvalidOperationException
{
    public PersistenceIntegrityException(string message)
        : base(message)
    {
    }
}
