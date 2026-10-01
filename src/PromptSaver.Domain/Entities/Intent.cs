using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Entities;

public sealed class Intent
{
    public static readonly IntentId ReservedUnsortedId =
        new(Guid.Parse("00000000-0000-7000-8000-000000000001"));

    private Intent(
        IntentId id,
        string canonicalName,
        string normalizedKey,
        string? description,
        IntentSource source,
        bool isArchived,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version,
        IntentId? mergedIntoIntentId)
    {
        Id = id;
        CanonicalName = canonicalName;
        NormalizedKey = normalizedKey;
        Description = description;
        Source = source;
        IsArchived = isArchived;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = updatedAtUtc;
        Version = version;
        MergedIntoIntentId = mergedIntoIntentId;
    }

    public IntentId Id { get; }

    public string CanonicalName { get; private set; }

    public string NormalizedKey { get; private set; }

    public string? Description { get; private set; }

    public IntentSource Source { get; }

    public bool IsArchived { get; private set; }

    public bool IsSystem => Source == IntentSource.System;

    public DateTimeOffset CreatedAtUtc { get; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public long Version { get; private set; }

    public IntentId? MergedIntoIntentId { get; private set; }

    public static Intent ReservedUnsorted =>
        new(
            ReservedUnsortedId,
            "Unsorted",
            "unsorted",
            "Prompts that need intent review.",
            IntentSource.System,
            false,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            0,
            null);

    public static Intent Create(
        IntentId id,
        string canonicalName,
        IntentSource source,
        DateTimeOffset createdAtUtc,
        string? description = null)
    {
        ValidateCanonicalName(canonicalName);
        ValidateDescription(description);

        if (id == ReservedUnsortedId && source != IntentSource.System)
        {
            throw new DomainValidationException(
                "intent.unsorted.reserved",
                "The reserved Unsorted identifier can only be used by the system.");
        }

        return new Intent(
            id,
            canonicalName,
            NormalizedText.CreateKey(canonicalName, "intent.name.required", nameof(canonicalName)),
            description,
            source,
            false,
            createdAtUtc,
            createdAtUtc,
            0,
            null);
    }

    public void Rename(string canonicalName, DateTimeOffset updatedAtUtc)
    {
        EnsureMutable();
        ValidateCanonicalName(canonicalName);
        CanonicalName = canonicalName;
        NormalizedKey = NormalizedText.CreateKey(
            canonicalName,
            "intent.name.required",
            nameof(canonicalName));
        Touch(updatedAtUtc);
    }

    public void Unarchive(DateTimeOffset updatedAtUtc)
    {
        if (Id == ReservedUnsortedId)
        {
            return;
        }

        IsArchived = false;
        MergedIntoIntentId = null;
        Touch(updatedAtUtc);
    }

    public void ArchiveAsMerged(IntentId targetId, DateTimeOffset updatedAtUtc)
    {
        if (Id == ReservedUnsortedId)
        {
            throw new DomainValidationException(
                "intent.unsorted.cannot_archive",
                "The reserved Unsorted intent cannot be archived.");
        }

        if (targetId == Id)
        {
            throw new DomainValidationException(
                "intent.merge.same_intent",
                "An intent cannot be merged into itself.");
        }

        IsArchived = true;
        MergedIntoIntentId = targetId;
        Touch(updatedAtUtc);
    }

    private static void ValidateCanonicalName(string canonicalName)
    {
        ArgumentNullException.ThrowIfNull(canonicalName);
        if (string.IsNullOrWhiteSpace(canonicalName))
        {
            throw new DomainValidationException(
                "intent.name.required",
                "Intent name cannot be empty or whitespace.",
                nameof(canonicalName));
        }

        if (canonicalName.Length is < 3 or > 160)
        {
            throw new DomainValidationException(
                "intent.name.length",
                "Intent name must be between 3 and 160 characters.",
                nameof(canonicalName));
        }
    }

    private static void ValidateDescription(string? description)
    {
        if (description is { Length: > 2000 })
        {
            throw new DomainValidationException(
                "intent.description.too_long",
                "Intent description cannot exceed 2000 characters.",
                nameof(description));
        }
    }

    private void EnsureMutable()
    {
        if (Id == ReservedUnsortedId)
        {
            throw new DomainValidationException(
                "intent.unsorted.immutable",
                "The reserved Unsorted intent cannot be modified.");
        }
    }

    private void Touch(DateTimeOffset updatedAtUtc)
    {
        UpdatedAtUtc = updatedAtUtc;
        Version++;
    }
}

public sealed record IntentAlias
{
    private IntentAlias(
        IntentAliasId id,
        IntentId intentId,
        string value,
        string normalizedKey,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        IntentId = intentId;
        Value = value;
        NormalizedKey = normalizedKey;
        CreatedAtUtc = createdAtUtc;
    }

    public IntentAliasId Id { get; }

    public IntentId IntentId { get; }

    public string Value { get; }

    public string NormalizedKey { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static IntentAlias Create(
        IntentAliasId id,
        IntentId intentId,
        string value,
        DateTimeOffset createdAtUtc)
    {
        string normalizedKey =
            NormalizedText.CreateKey(value, "intent_alias.value.required", nameof(value));

        if (value.Length > 160)
        {
            throw new DomainValidationException(
                "intent_alias.value.too_long",
                "Intent alias cannot exceed 160 characters.",
                nameof(value));
        }

        return new IntentAlias(id, intentId, value, normalizedKey, createdAtUtc);
    }
}
