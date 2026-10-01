namespace PromptSaver.Domain.ValueObjects;

public readonly record struct PromptId
{
    public PromptId(Guid value)
    {
        Value = EnsureNotEmpty(value, nameof(PromptId));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();

    private static Guid EnsureNotEmpty(Guid value, string typeName)
    {
        return value != Guid.Empty
            ? value
            : throw new DomainValidationException(
                "id.empty",
                $"{typeName} cannot be empty.",
                nameof(value));
    }
}

public readonly record struct IntentId
{
    public IntentId(Guid value)
    {
        Value = value != Guid.Empty
            ? value
            : throw new DomainValidationException("id.empty", "IntentId cannot be empty.", nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct IntentAliasId
{
    public IntentAliasId(Guid value)
    {
        Value = value != Guid.Empty
            ? value
            : throw new DomainValidationException("id.empty", "IntentAliasId cannot be empty.", nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct SkillId
{
    public SkillId(Guid value)
    {
        Value = value != Guid.Empty
            ? value
            : throw new DomainValidationException("id.empty", "SkillId cannot be empty.", nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct EntityId
{
    public EntityId(Guid value)
    {
        Value = value != Guid.Empty
            ? value
            : throw new DomainValidationException("id.empty", "EntityId cannot be empty.", nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}

public readonly record struct ProviderConfigurationId
{
    public ProviderConfigurationId(Guid value)
    {
        Value = value != Guid.Empty
            ? value
            : throw new DomainValidationException(
                "id.empty",
                "ProviderConfigurationId cannot be empty.",
                nameof(value));
    }

    public Guid Value { get; }

    public override string ToString() => Value.ToString();
}
