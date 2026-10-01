namespace PromptSaver.Domain.Entities;

public enum TitleSource
{
    Derived,
    Provider,
    User,
}

public enum IntentAssignment
{
    Unsorted,
    Deterministic,
    AutoMapped,
    Created,
    UserSelected,
    UserCorrected,
    Merged,
}

public enum IntentReviewState
{
    NotRequired,
    NeedsReview,
    Skipped,
    Resolved,
}

public enum MetadataStatus
{
    LocalComplete,
    PendingEnrichment,
    EnrichmentFailed,
    ProposalReady,
    EnrichmentProcessing,
}

public enum IntentSource
{
    System,
    Deterministic,
    Provider,
    User,
}

public enum MetadataSource
{
    Deterministic,
    Provider,
    User,
}

public enum EntityType
{
    Person,
    Organization,
    Product,
    Technology,
    Location,
    Document,
    Other,
}
