using PromptSaver.Application.Dtos;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.UseCases;

public interface ISaveCaptureDraft
{
    Task<AppResult> ExecuteAsync(CaptureDraftDto draft, CancellationToken cancellationToken);
}

public interface IRecoverCaptureDraft
{
    Task<CaptureDraftRecoveryResult> ExecuteAsync(CancellationToken cancellationToken);
}

public interface IDiscardCaptureDraft
{
    Task<AppResult> ExecuteAsync(CancellationToken cancellationToken);
}

public interface ICapturePrompt
{
    Task<AppResult<CapturePromptResult>> ExecuteAsync(
        CapturePromptCommand command,
        CancellationToken cancellationToken);
}

public interface ISavePromptEditDraft
{
    Task<AppResult> ExecuteAsync(PromptEditDraftDto draft, CancellationToken cancellationToken);
}

public interface ICommitPromptEdit
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        CommitPromptEditCommand command,
        CancellationToken cancellationToken);
}

public interface IDiscardPromptEditDraft
{
    Task<AppResult> ExecuteAsync(PromptId promptId, CancellationToken cancellationToken);
}

public interface IGetPromptDetails
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        PromptId promptId,
        CancellationToken cancellationToken);
}

public interface ICopyPrompt
{
    Task<AppResult> ExecuteAsync(CopyPromptCommand command, CancellationToken cancellationToken);
}

public interface IDuplicatePrompt
{
    Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        DuplicatePromptCommand command,
        CancellationToken cancellationToken);
}

public interface IDeletePrompt
{
    Task<AppResult<DeletePromptResult>> ExecuteAsync(
        DeletePromptCommand command,
        CancellationToken cancellationToken);
}

public interface ISearchPrompts
{
    Task<AppResult<SearchPageDto<PromptSummaryDto>>> ExecuteAsync(
        SearchPromptsQuery query,
        CancellationToken cancellationToken);
}
