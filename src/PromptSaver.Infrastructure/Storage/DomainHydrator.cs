using System.Reflection;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Infrastructure.Storage;

internal static class DomainHydrator
{
    private static readonly ConstructorInfo PromptConstructor =
        typeof(Prompt).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(PromptId),
                typeof(PromptBody),
                typeof(PromptTitle),
                typeof(TitleSource),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset),
                typeof(IntentId),
                typeof(IntentAssignment),
                typeof(IntentReviewState),
                typeof(ScoreBasisPoints?),
                typeof(string),
                typeof(MetadataStatus),
                typeof(long),
            ],
            modifiers: null) ??
        throw new InvalidOperationException("Prompt persistence constructor was not found.");

    private static readonly ConstructorInfo IntentConstructor =
        typeof(Intent).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(IntentId),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(IntentSource),
                typeof(bool),
                typeof(DateTimeOffset),
                typeof(DateTimeOffset),
                typeof(long),
                typeof(IntentId?),
            ],
            modifiers: null) ??
        throw new InvalidOperationException("Intent persistence constructor was not found.");

    private static readonly FieldInfo PromptLastCopiedField = PromptField("<LastCopiedAtUtc>k__BackingField");
    private static readonly FieldInfo PromptCopyCountField = PromptField("<CopyCount>k__BackingField");
    private static readonly FieldInfo PromptContentHashField = PromptField("<ContentHash>k__BackingField");
    private static readonly FieldInfo PromptSkillsField = PromptField("_skills");
    private static readonly FieldInfo PromptEntitiesField = PromptField("_entities");

    internal static Prompt Prompt(
        PromptId id,
        PromptBody body,
        PromptTitle? title,
        TitleSource titleSource,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        DateTimeOffset? lastCopiedAtUtc,
        long copyCount,
        IntentId intentId,
        IntentAssignment intentAssignment,
        IntentReviewState intentReviewState,
        ScoreBasisPoints? intentScore,
        string? scoringAlgorithmVersion,
        MetadataStatus metadataStatus,
        byte[] contentHash,
        long version,
        IReadOnlyList<PromptSkill> skills,
        IReadOnlyList<PromptEntity> entities)
    {
        Prompt prompt = (Prompt)PromptConstructor.Invoke(
        [
            id,
            body,
            title,
            titleSource,
            createdAtUtc,
            updatedAtUtc,
            intentId,
            intentAssignment,
            intentReviewState,
            intentScore,
            scoringAlgorithmVersion,
            metadataStatus,
            version,
        ]);
        PromptLastCopiedField.SetValue(prompt, lastCopiedAtUtc);
        PromptCopyCountField.SetValue(prompt, copyCount);
        PromptContentHashField.SetValue(prompt, contentHash);
        ((List<PromptSkill>)PromptSkillsField.GetValue(prompt)!).AddRange(skills);
        ((List<PromptEntity>)PromptEntitiesField.GetValue(prompt)!).AddRange(entities);
        return prompt;
    }

    internal static Intent Intent(
        IntentId id,
        string canonicalName,
        string normalizedKey,
        string? description,
        IntentSource source,
        bool isArchived,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc,
        long version,
        IntentId? mergedIntoIntentId) =>
        (Intent)IntentConstructor.Invoke(
        [
            id,
            canonicalName,
            normalizedKey,
            description,
            source,
            isArchived,
            createdAtUtc,
            updatedAtUtc,
            version,
            mergedIntoIntentId,
        ]);

    private static FieldInfo PromptField(string name) =>
        typeof(Prompt).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException($"Prompt persistence field '{name}' was not found.");
}
