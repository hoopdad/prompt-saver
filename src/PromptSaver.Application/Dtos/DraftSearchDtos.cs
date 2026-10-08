using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Dtos;

public sealed record CaptureDraftDto
{
    private CaptureDraftDto(
        int schemaVersion,
        string body,
        int caretOffset,
        int selectionLength,
        DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion;
        Body = body;
        CaretOffset = caretOffset;
        SelectionLength = selectionLength;
        UpdatedAtUtc = updatedAtUtc;
    }

    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; }

    public string Body { get; }

    public int CaretOffset { get; }

    public int SelectionLength { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    public static CaptureDraftDto Create(
        string body,
        int caretOffset,
        int selectionLength,
        DateTimeOffset updatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (caretOffset < 0 ||
            selectionLength < 0 ||
            caretOffset > body.Length ||
            selectionLength > body.Length - caretOffset)
        {
            throw new AppContractException(
                "draft.selection.out_of_range",
                "Draft caret and selection must be within the body.",
                nameof(caretOffset));
        }

        return new CaptureDraftDto(
            CurrentSchemaVersion,
            body,
            caretOffset,
            selectionLength,
            updatedAtUtc);
    }
}

public enum DraftRecoveryStatus
{
    Missing,
    Recovered,
    Corrupt,
}

public sealed record CaptureDraftRecoveryResult(
    DraftRecoveryStatus Status,
    CaptureDraftDto? Draft,
    string? PreservedCorruptFilePath,
    AppError? Error);

public sealed record SearchPromptsQuery
{
    public SearchPromptsQuery(
        string text,
        int pageNumber,
        int pageSize,
        IntentId? intentId = null,
        bool needsReviewOnly = false,
        DateTimeOffset? createdFromUtc = null,
        DateTimeOffset? createdToUtc = null,
        PromptSortColumn sortColumn = PromptSortColumn.Intent,
        PromptSortDirection sortDirection = PromptSortDirection.Ascending)
    {
        if (pageNumber < 1)
        {
            throw new AppContractException(
                "search.page.invalid",
                "Page number must be at least 1.",
                nameof(pageNumber));
        }

        if (pageSize is < 1 or > 200)
        {
            throw new AppContractException(
                "search.page_size.invalid",
                "Page size must be between 1 and 200.",
                nameof(pageSize));
        }

        if (!Enum.IsDefined(sortColumn))
        {
            throw new AppContractException(
                "search.sort_column.invalid",
                "Sort column is invalid.",
                nameof(sortColumn));
        }

        if (!Enum.IsDefined(sortDirection))
        {
            throw new AppContractException(
                "search.sort_direction.invalid",
                "Sort direction is invalid.",
                nameof(sortDirection));
        }

        Text = text ?? string.Empty;
        PageNumber = pageNumber;
        PageSize = pageSize;
        IntentId = intentId;
        NeedsReviewOnly = needsReviewOnly;
        CreatedFromUtc = createdFromUtc;
        CreatedToUtc = createdToUtc;
        SortColumn = sortColumn;
        SortDirection = sortDirection;
    }

    public string Text { get; }

    public int PageNumber { get; }

    public int PageSize { get; }

    public IntentId? IntentId { get; }

    public bool NeedsReviewOnly { get; }

    public DateTimeOffset? CreatedFromUtc { get; }

    public DateTimeOffset? CreatedToUtc { get; }

    public PromptSortColumn SortColumn { get; }

    public PromptSortDirection SortDirection { get; }
}

public enum PromptSortColumn
{
    Intent,
    Title,
    Prompt,
    Modified,
    Copies,
}

public enum PromptSortDirection
{
    Ascending,
    Descending,
}

public sealed record SearchPageDto<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    long TotalCount);
