using PromptSaver.Application;
using PromptSaver.Application.Dtos;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.Tests;

public sealed class ApplicationContractTests
{
    [Fact]
    public void ResultCarriesTypedErrorWithoutSuccessShapedFallback()
    {
        AppError error = new(
            AppErrorCode.Validation,
            "prompt.body.required",
            "A prompt body is required.");

        AppResult<PromptDetailsDto> result = AppResult.Failure<PromptDetailsDto>(error);

        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
        Assert.Throws<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void CaptureDraftDtoPreservesExactTextAndSelection()
    {
        const string body = "  exact\r\ntext  ";
        CaptureDraftDto draft = CaptureDraftDto.Create(
            body,
            caretOffset: 4,
            selectionLength: 3,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(body, draft.Body);
        Assert.Equal(4, draft.CaretOffset);
        Assert.Equal(3, draft.SelectionLength);
    }

    [Fact]
    public void CaptureDraftDtoRejectsSelectionOutsideBody()
    {
        AppContractException exception =
            Assert.Throws<AppContractException>(
                () => CaptureDraftDto.Create(
                    "abc",
                    caretOffset: 2,
                    selectionLength: 2,
                    DateTimeOffset.UtcNow));

        Assert.Equal("draft.selection.out_of_range", exception.Code);
    }

    [Fact]
    public void SearchPageRejectsInvalidPaging()
    {
        Assert.Throws<AppContractException>(
            () => new SearchPromptsQuery("text", pageNumber: 0, pageSize: 20));
        Assert.Throws<AppContractException>(
            () => new SearchPromptsQuery("text", pageNumber: 1, pageSize: 0));
    }

    [Fact]
    public void CaptureCommandPreservesBodyUntilDomainValidation()
    {
        const string body = "  exact\r\ntext  ";

        CapturePromptCommand command = new(body, "Title");

        Assert.Equal(body, command.Body);
        Assert.Equal("Title", command.Title);
    }
}
