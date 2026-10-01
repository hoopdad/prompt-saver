using PromptSaver.Domain.Entities;

namespace PromptSaver.Domain.Policies;

public static class PromptDeletionPolicy
{
    public static void EnsurePermanentDeletionConfirmed(bool isConfirmed)
    {
        if (!isConfirmed)
        {
            throw new DomainValidationException(
                "prompt.delete.confirmation_required",
                "Permanent prompt deletion requires explicit confirmation.");
        }
    }
}

public static class IntentMergePolicy
{
    public static void Validate(Intent source, Intent target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        if (source.Id == target.Id)
        {
            throw new DomainValidationException(
                "intent.merge.same_intent",
                "An intent cannot be merged into itself.");
        }

        if (source.Id == Intent.ReservedUnsortedId || target.Id == Intent.ReservedUnsortedId)
        {
            throw new DomainValidationException(
                "intent.merge.unsorted_forbidden",
                "The reserved Unsorted intent cannot be a merge source or target.");
        }

        if (source.IsArchived)
        {
            throw new DomainValidationException(
                "intent.merge.source_archived",
                "An archived intent cannot be merged again.");
        }

        if (target.IsArchived)
        {
            throw new DomainValidationException(
                "intent.merge.target_archived",
                "An archived intent cannot receive a merge.");
        }
    }
}
