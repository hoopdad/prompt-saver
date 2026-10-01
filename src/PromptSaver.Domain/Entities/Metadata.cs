using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Domain.Entities;

public sealed record Skill
{
    private Skill(SkillId id, string name, string normalizedKey)
    {
        Id = id;
        Name = name;
        NormalizedKey = normalizedKey;
    }

    public SkillId Id { get; }

    public string Name { get; }

    public string NormalizedKey { get; }

    public static Skill Create(SkillId id, string name)
    {
        string key = NormalizedText.CreateKey(name, "skill.name.required", nameof(name));
        if (name.Length > 160)
        {
            throw new DomainValidationException(
                "skill.name.too_long",
                "Skill name cannot exceed 160 characters.",
                nameof(name));
        }

        return new Skill(id, name, key);
    }
}

public sealed record Entity
{
    private Entity(EntityId id, string name, string normalizedKey, EntityType type)
    {
        Id = id;
        Name = name;
        NormalizedKey = normalizedKey;
        Type = type;
    }

    public EntityId Id { get; }

    public string Name { get; }

    public string NormalizedKey { get; }

    public EntityType Type { get; }

    public static Entity Create(EntityId id, string name, EntityType type)
    {
        string key = NormalizedText.CreateKey(name, "entity.name.required", nameof(name));
        if (name.Length > 160)
        {
            throw new DomainValidationException(
                "entity.name.too_long",
                "Entity name cannot exceed 160 characters.",
                nameof(name));
        }

        return new Entity(id, name, key, type);
    }
}

public sealed record PromptSkill
{
    private PromptSkill(
        PromptId promptId,
        SkillId skillId,
        MetadataSource source,
        Confidence confidence,
        string extractorVersion)
    {
        PromptId = promptId;
        SkillId = skillId;
        Source = source;
        Confidence = confidence;
        ExtractorVersion = extractorVersion;
    }

    public PromptId PromptId { get; }

    public SkillId SkillId { get; }

    public MetadataSource Source { get; }

    public Confidence Confidence { get; }

    public string ExtractorVersion { get; }

    public static PromptSkill Create(
        PromptId promptId,
        SkillId skillId,
        MetadataSource source,
        Confidence confidence,
        string extractorVersion)
    {
        EnsureExtractorVersion(extractorVersion);
        return new PromptSkill(promptId, skillId, source, confidence, extractorVersion);
    }

    public PromptSkill ForPrompt(PromptId promptId) =>
        new(promptId, SkillId, Source, Confidence, ExtractorVersion);

    private static void EnsureExtractorVersion(string extractorVersion)
    {
        if (string.IsNullOrWhiteSpace(extractorVersion))
        {
            throw new DomainValidationException(
                "metadata.extractor_version.required",
                "Extractor version is required.",
                nameof(extractorVersion));
        }
    }
}

public sealed record PromptEntity
{
    private PromptEntity(
        PromptId promptId,
        EntityId entityId,
        MetadataSource source,
        Confidence confidence,
        string extractorVersion)
    {
        PromptId = promptId;
        EntityId = entityId;
        Source = source;
        Confidence = confidence;
        ExtractorVersion = extractorVersion;
    }

    public PromptId PromptId { get; }

    public EntityId EntityId { get; }

    public MetadataSource Source { get; }

    public Confidence Confidence { get; }

    public string ExtractorVersion { get; }

    public static PromptEntity Create(
        PromptId promptId,
        EntityId entityId,
        MetadataSource source,
        Confidence confidence,
        string extractorVersion)
    {
        if (string.IsNullOrWhiteSpace(extractorVersion))
        {
            throw new DomainValidationException(
                "metadata.extractor_version.required",
                "Extractor version is required.",
                nameof(extractorVersion));
        }

        return new PromptEntity(promptId, entityId, source, confidence, extractorVersion);
    }

    public PromptEntity ForPrompt(PromptId promptId) =>
        new(promptId, EntityId, Source, Confidence, ExtractorVersion);
}
