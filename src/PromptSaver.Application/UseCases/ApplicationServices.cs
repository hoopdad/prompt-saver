using PromptSaver.Application.Dtos;
using PromptSaver.Application.Policies;
using PromptSaver.Application.Ports;
using PromptSaver.Domain;
using PromptSaver.Domain.Entities;
using PromptSaver.Domain.Policies;
using PromptSaver.Domain.ValueObjects;

namespace PromptSaver.Application.UseCases;

public sealed class PromptApplicationService :
    ISaveCaptureDraft,
    IRecoverCaptureDraft,
    IDiscardCaptureDraft,
    ICapturePrompt,
    ISavePromptEditDraft,
    ICommitPromptEdit,
    IDiscardPromptEditDraft,
    IGetPromptDetails,
    ICopyPrompt,
    IDuplicatePrompt,
    IDeletePrompt,
    ISearchPrompts,
    IAssignIntent,
    IReviewIntentAssignment,
    IMergeIntents,
    IListIntents,
    IManageMetadata,
    IQueryPromptIntent,
    ICreateBackup,
    IRestoreBackup,
    IRebuildSearchIndex,
    IGetDiagnostics
{
    private readonly ICaptureDraftStore _captureDrafts;
    private readonly IPromptEditDraftStore _editDrafts;
    private readonly IUnitOfWorkFactory _units;
    private readonly IPromptSearch _search;
    private readonly IPromptPersistenceOperations _operations;
    private readonly IClipboard _clipboard;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IBackupService _backups;
    private readonly IDataRootResolver _dataRoot;
    private readonly IBackgroundScheduler _background;
    private readonly IEnrichPendingPrompts? _enrichment;
    private readonly IProviderConfigurationStore? _providerConfigurations;

    public PromptApplicationService(
        ICaptureDraftStore captureDrafts,
        IPromptEditDraftStore editDrafts,
        IUnitOfWorkFactory units,
        IPromptSearch search,
        IPromptPersistenceOperations operations,
        IClipboard clipboard,
        IClock clock,
        IIdGenerator ids,
        IBackupService backups,
        IDataRootResolver dataRoot,
        IBackgroundScheduler background,
        IEnrichPendingPrompts? enrichment = null,
        IProviderConfigurationStore? providerConfigurations = null)
    {
        _captureDrafts = captureDrafts;
        _editDrafts = editDrafts;
        _units = units;
        _search = search;
        _operations = operations;
        _clipboard = clipboard;
        _clock = clock;
        _ids = ids;
        _backups = backups;
        _dataRoot = dataRoot;
        _background = background;
        _enrichment = enrichment;
        _providerConfigurations = providerConfigurations;
    }

    public Task<AppResult> ExecuteAsync(CaptureDraftDto draft, CancellationToken cancellationToken) =>
        _captureDrafts.SaveAsync(draft, cancellationToken);

    Task<CaptureDraftRecoveryResult> IRecoverCaptureDraft.ExecuteAsync(CancellationToken cancellationToken) =>
        _captureDrafts.RecoverAsync(cancellationToken);

    Task<AppResult> IDiscardCaptureDraft.ExecuteAsync(CancellationToken cancellationToken) =>
        _captureDrafts.DeleteAsync(cancellationToken);

    public Task<AppResult> ExecuteAsync(PromptEditDraftDto draft, CancellationToken cancellationToken) =>
        _editDrafts.SaveAsync(draft, cancellationToken);

    Task<AppResult> IDiscardPromptEditDraft.ExecuteAsync(
        PromptId promptId,
        CancellationToken cancellationToken) =>
        _editDrafts.DeleteAsync(promptId, cancellationToken);

    public async Task<AppResult<CapturePromptResult>> ExecuteAsync(
        CapturePromptCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            PromptBody body = PromptBody.Create(command.Body);
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<CapturePromptResult>(begin.Error!);
            }

            await using IAppUnitOfWork unit = begin.Value;
            IReadOnlyList<Intent> intents = await unit.Intents.ListActiveAsync(cancellationToken);
            IReadOnlyList<IntentAlias> aliases = await unit.Intents.ListAliasesAsync(cancellationToken);
            string? normalizedCandidate =
                DeterministicMetadataExtractor.Extract(command.Body).IntentCandidate is string candidate
                    ? IntentTextNormalizer.NormalizeKey(candidate)
                    : null;
            Intent? archivedIdentity = normalizedCandidate is null
                ? null
                : await unit.Intents.FindByNormalizedKeyAsync(
                    normalizedCandidate,
                    includeArchived: true,
                    cancellationToken);
            IReadOnlyList<Intent> allIntents = archivedIdentity is null ||
                intents.Any(intent => intent.Id == archivedIdentity.Id)
                    ? intents
                    : [.. intents, archivedIdentity];
            IntentResolutionResult resolution =
                new IntentResolutionEngine(_ids, _clock).Resolve(
                    command.Body,
                    intents,
                    allIntents,
                    aliases,
                    archivedIdentity);
            if (resolution.CreatedIntent is not null)
            {
                await unit.Intents.AddAsync(resolution.CreatedIntent, cancellationToken);
            }
            else if (resolution.UnarchivedExistingIdentity)
            {
                await unit.Intents.UpdateAsync(
                    resolution.SelectedIntent,
                    resolution.SelectedIntent.Version - 1,
                    cancellationToken);
            }

            PromptId promptId = _ids.NewPromptId();
            PromptTitle? title = PromptTitle.Create(
                string.IsNullOrWhiteSpace(command.Title)
                    ? resolution.Metadata.Title
                    : command.Title);
            TitleSource titleSource =
                string.IsNullOrWhiteSpace(command.Title) ? TitleSource.Derived : TitleSource.User;
            bool enrichmentEnabled = false;
            if (_providerConfigurations is not null)
            {
                AppResult<ProviderConfigurationDto?> configured =
                    await _providerConfigurations.GetEnabledAsync(cancellationToken);
                enrichmentEnabled = configured.IsSuccess && configured.Value is not null;
            }

            Prompt prompt = Prompt.Create(
                promptId,
                body,
                title,
                titleSource,
                _clock.UtcNow,
                resolution.SelectedIntent.Id,
                resolution.Decision.Assignment,
                resolution.Decision.ReviewState,
                resolution.Decision.BestCandidate?.Score,
                resolution.Decision.BestCandidate is null
                    ? null
                    : IntentLexicalScorer.AlgorithmVersion,
                enrichmentEnabled
                    ? MetadataStatus.PendingEnrichment
                    : MetadataStatus.LocalComplete);

            (
                IReadOnlyList<PromptSkill> skills,
                IReadOnlyList<PromptEntity> entities,
                IReadOnlyList<SkillDto> skillDtos,
                IReadOnlyList<EntityDto> entityDtos) =
                await PersistMetadataAsync(unit, promptId, resolution.Metadata, cancellationToken);
            prompt.SetIntentCandidates(resolution.ReviewCandidates);
            await unit.Prompts.AddAsync(prompt, cancellationToken);
            await unit.Metadata.ReplacePromptMetadataAsync(promptId, skills, entities, cancellationToken);
            await unit.IntentCandidates.ReplaceAsync(
                promptId,
                resolution.ReviewCandidates,
                cancellationToken);
            AppResult committed = await unit.CommitAsync(cancellationToken);
            if (!committed.IsSuccess)
            {
                return AppResult.Failure<CapturePromptResult>(committed.Error!);
            }

            AppResult draftDeleted = await _captureDrafts.DeleteAsync(cancellationToken);
            bool enrichmentQueued = false;
            if (_enrichment is not null && enrichmentEnabled)
            {
                enrichmentQueued = _background.TrySchedule(
                    "provider-enrichment",
                    token => _enrichment.ExecuteAsync(token)).IsSuccess;
            }

            PromptDetailsDto details = CreateCommittedDetails(
                prompt,
                resolution,
                skillDtos,
                entityDtos,
                intents);
            return AppResult.Success(
                new CapturePromptResult(
                    details,
                    draftDeleted.IsSuccess,
                    enrichmentQueued));
        }
        catch (Exception exception) when (
            exception is DomainValidationException or AppContractException)
        {
            return Validation<CapturePromptResult>(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            return Persistence<CapturePromptResult>("prompt.save_failed", exception);
        }
    }

    public async Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        CommitPromptEditCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<PromptDetailsDto>(begin.Error!);
            }

            await using IAppUnitOfWork unit = begin.Value;
            Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
            if (prompt is null)
            {
                return NotFound<PromptDetailsDto>();
            }

            if (prompt.Version != command.BaseVersion)
            {
                return Conflict<PromptDetailsDto>("prompt.edit_conflict");
            }

            prompt.UpdateContent(
                PromptBody.Create(command.Body),
                PromptTitle.Create(command.Title),
                string.IsNullOrWhiteSpace(command.Title) ? TitleSource.Derived : TitleSource.User,
                _clock.UtcNow);
            await unit.Prompts.UpdateAsync(prompt, command.BaseVersion, cancellationToken);
            AppResult committed = await unit.CommitAsync(cancellationToken);
            if (!committed.IsSuccess)
            {
                return AppResult.Failure<PromptDetailsDto>(committed.Error!);
            }

            await _editDrafts.DeleteAsync(command.PromptId, cancellationToken);
            return await GetDetailsAsync(command.PromptId, cancellationToken);
        }
        catch (DomainValidationException exception)
        {
            return Validation<PromptDetailsDto>(exception.Message);
        }
        catch (InvalidOperationException)
        {
            return Conflict<PromptDetailsDto>("prompt.edit_conflict");
        }
    }

    public Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        PromptId promptId,
        CancellationToken cancellationToken) =>
        GetDetailsAsync(promptId, cancellationToken);

    public async Task<AppResult> ExecuteAsync(
        CopyPromptCommand command,
        CancellationToken cancellationToken)
    {
        AppResult copied = await _clipboard.SetTextAsync(command.Text, cancellationToken);
        if (!copied.IsSuccess || command.PromptId is null)
        {
            return copied;
        }

        AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
        if (!begin.IsSuccess)
        {
            return begin.Error!.Code == AppErrorCode.PersistenceUnavailable
                ? AppResult.Success()
                : AppResult.Failure(begin.Error);
        }

        await using IAppUnitOfWork unit = begin.Value;
        Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId.Value, cancellationToken);
        if (prompt is null)
        {
            await unit.RollbackAsync(cancellationToken);
            return AppResult.Success();
        }

        long expectedVersion = prompt.Version;
        prompt.RecordSuccessfulCopy(_clock.UtcNow);
        await unit.Prompts.UpdateAsync(prompt, expectedVersion, cancellationToken);
        AppResult committed = await unit.CommitAsync(cancellationToken);
        return committed.IsSuccess ? AppResult.Success() : committed;
    }

    public async Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        DuplicatePromptCommand command,
        CancellationToken cancellationToken)
    {
        PromptId duplicateId = _ids.NewPromptId();
        AppResult<PromptId> result = await _operations.DuplicateAsync(
            command.PromptId,
            duplicateId,
            _clock.UtcNow,
            cancellationToken);
        return result.IsSuccess
            ? await GetDetailsAsync(duplicateId, cancellationToken)
            : AppResult.Failure<PromptDetailsDto>(result.Error!);
    }

    public async Task<AppResult<DeletePromptResult>> ExecuteAsync(
        DeletePromptCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            PromptDeletionPolicy.EnsurePermanentDeletionConfirmed(command.IsConfirmed);
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<DeletePromptResult>(begin.Error!);
            }

            long version;
            await using (IAppUnitOfWork unit = begin.Value)
            {
                Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
                if (prompt is null)
                {
                    return NotFound<DeletePromptResult>();
                }

                version = prompt.Version;
                await unit.RollbackAsync(cancellationToken);
            }

            AppResult deleted =
                await _operations.DeleteAsync(command.PromptId, version, cancellationToken);
            return deleted.IsSuccess
                ? AppResult.Success(new DeletePromptResult(command.PromptId, null))
                : AppResult.Failure<DeletePromptResult>(deleted.Error!);
        }
        catch (DomainValidationException exception)
        {
            return Validation<DeletePromptResult>(exception.Message);
        }
    }

    public Task<AppResult<SearchPageDto<PromptSummaryDto>>> ExecuteAsync(
        SearchPromptsQuery query,
        CancellationToken cancellationToken) =>
        _search.SearchAsync(query, cancellationToken);

    public Task<AppResult<PromptIntentSuggestionDto>> ExecuteAsync(
        QueryPromptIntentCommand command,
        CancellationToken cancellationToken) =>
        _enrichment is IQueryPromptIntent query
            ? query.ExecuteAsync(command, cancellationToken)
            : Task.FromResult(
                AppResult.Failure<PromptIntentSuggestionDto>(
                    new AppError(
                        AppErrorCode.ProviderUnavailable,
                        "provider.intent.unavailable",
                        "Enable an LLM provider in Settings before asking for an intent.",
                        true)));

    public async Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        AssignIntentCommand command,
        CancellationToken cancellationToken)
    {
        AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
        if (!begin.IsSuccess)
        {
            return AppResult.Failure<PromptDetailsDto>(begin.Error!);
        }

        await using IAppUnitOfWork unit = begin.Value;
        Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
        Intent? intent = await unit.Intents.GetAsync(command.IntentId, cancellationToken);
        if (prompt is null || intent is null)
        {
            return NotFound<PromptDetailsDto>();
        }

        if (prompt.Version != command.PromptVersion)
        {
            return Conflict<PromptDetailsDto>("prompt.intent_conflict");
        }

        IntentMutationCoordinator.Reclassify(prompt, intent, command.Assignment, _clock.UtcNow);
        await unit.Prompts.UpdateAsync(prompt, command.PromptVersion, cancellationToken);
        await unit.IntentCandidates.ReplaceAsync(prompt.Id, [], cancellationToken);
        AppResult committed = await unit.CommitAsync(cancellationToken);
        return committed.IsSuccess
            ? await GetDetailsAsync(prompt.Id, cancellationToken)
            : AppResult.Failure<PromptDetailsDto>(committed.Error!);
    }

    public async Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        ReviewIntentAssignmentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Action == IntentReviewAction.ReviewLater)
        {
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<PromptDetailsDto>(begin.Error!);
            }

            await using IAppUnitOfWork unit = begin.Value;
            Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
            if (prompt is null)
            {
                return NotFound<PromptDetailsDto>();
            }

            if (prompt.Version != command.PromptVersion)
            {
                return Conflict<PromptDetailsDto>("prompt.intent_conflict");
            }

            prompt.ReviewLater();
            await unit.Prompts.UpdateAsync(prompt, command.PromptVersion, cancellationToken);
            AppResult committed = await unit.CommitAsync(cancellationToken);
            return committed.IsSuccess
                ? await GetDetailsAsync(prompt.Id, cancellationToken)
                : AppResult.Failure<PromptDetailsDto>(committed.Error!);
        }

        if (command.Action == IntentReviewAction.KeepProvisional)
        {
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<PromptDetailsDto>(begin.Error!);
            }

            await using IAppUnitOfWork unit = begin.Value;
            Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
            Intent? intent = prompt is null
                ? null
                : await unit.Intents.GetAsync(prompt.IntentId, cancellationToken);
            if (prompt is null || intent is null)
            {
                return NotFound<PromptDetailsDto>();
            }

            if (prompt.Version != command.PromptVersion)
            {
                return Conflict<PromptDetailsDto>("prompt.intent_conflict");
            }

            IntentMutationCoordinator.Reclassify(
                prompt,
                intent,
                IntentAssignment.UserSelected,
                _clock.UtcNow);
            await unit.Prompts.UpdateAsync(prompt, command.PromptVersion, cancellationToken);
            await unit.IntentCandidates.ReplaceAsync(prompt.Id, [], cancellationToken);
            AppResult committed = await unit.CommitAsync(cancellationToken);
            return committed.IsSuccess
                ? await GetDetailsAsync(prompt.Id, cancellationToken)
                : AppResult.Failure<PromptDetailsDto>(committed.Error!);
        }

        if (command.SelectedIntentId is null)
        {
            return Validation<PromptDetailsDto>("Choose an intent before completing review.");
        }

        return await ExecuteAsync(
            new AssignIntentCommand(
                command.PromptId,
                command.SelectedIntentId.Value,
                IntentAssignment.UserCorrected,
                command.PromptVersion),
            cancellationToken);
    }

    public async Task<AppResult<PromptDetailsDto>> ExecuteAsync(
        ManageMetadataCommand command,
        CancellationToken cancellationToken)
    {
        try
        {
            AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
            if (!begin.IsSuccess)
            {
                return AppResult.Failure<PromptDetailsDto>(begin.Error!);
            }

            await using IAppUnitOfWork unit = begin.Value;
            Prompt? prompt = await unit.Prompts.GetAsync(command.PromptId, cancellationToken);
            if (prompt is null)
            {
                return NotFound<PromptDetailsDto>();
            }

            if (prompt.Version != command.PromptVersion)
            {
                return Conflict<PromptDetailsDto>("prompt.metadata_conflict");
            }

            List<PromptSkill> skills = [];
            foreach (SkillSelectionDto selection in command.Skills)
            {
                Skill? skill = selection.Id is SkillId id
                    ? await unit.Metadata.GetSkillAsync(id, cancellationToken)
                    : await unit.Metadata.FindSkillByNormalizedKeyAsync(
                        NormalizedText.CreateKey(
                            selection.Name,
                            "skill.name.required",
                            nameof(selection.Name)),
                        cancellationToken);
                if (selection.Id is not null && skill is null)
                {
                    return NotFound<PromptDetailsDto>();
                }

                if (skill is null)
                {
                    skill = Skill.Create(_ids.NewSkillId(), selection.Name.Trim());
                    await unit.Metadata.AddSkillAsync(skill, cancellationToken);
                }

                if (skills.All(item => item.SkillId != skill.Id))
                {
                    skills.Add(PromptSkill.Create(
                        prompt.Id,
                        skill.Id,
                        MetadataSource.User,
                        Confidence.Create(1),
                        "user-v1"));
                }
            }

            List<PromptEntity> entities = [];
            foreach (EntitySelectionDto selection in command.Entities)
            {
                Entity? entity = selection.Id is EntityId id
                    ? await unit.Metadata.GetEntityAsync(id, cancellationToken)
                    : await unit.Metadata.FindEntityByNormalizedKeyAsync(
                        NormalizedText.CreateKey(
                            selection.Name,
                            "entity.name.required",
                            nameof(selection.Name)),
                        selection.Type,
                        cancellationToken);
                if (selection.Id is not null && entity is null)
                {
                    return NotFound<PromptDetailsDto>();
                }

                if (entity is null)
                {
                    entity = Entity.Create(
                        _ids.NewEntityId(),
                        selection.Name.Trim(),
                        selection.Type);
                    await unit.Metadata.AddEntityAsync(entity, cancellationToken);
                }

                if (entities.All(item => item.EntityId != entity.Id))
                {
                    entities.Add(PromptEntity.Create(
                        prompt.Id,
                        entity.Id,
                        MetadataSource.User,
                        Confidence.Create(1),
                        "user-v1"));
                }
            }

            long expectedVersion = prompt.Version;
            prompt.MarkMetadataEdited(_clock.UtcNow);
            await unit.Prompts.UpdateAsync(prompt, expectedVersion, cancellationToken);
            await unit.Metadata.ReplacePromptMetadataAsync(
                prompt.Id,
                skills,
                entities,
                cancellationToken);
            AppResult committed = await unit.CommitAsync(cancellationToken);
            return committed.IsSuccess
                ? await GetDetailsAsync(prompt.Id, cancellationToken)
                : AppResult.Failure<PromptDetailsDto>(committed.Error!);
        }
        catch (DomainValidationException exception)
        {
            return Validation<PromptDetailsDto>(exception.Message);
        }
        catch (InvalidOperationException)
        {
            return Conflict<PromptDetailsDto>("prompt.metadata_conflict");
        }
    }

    public Task<AppResult<MergeIntentsResult>> ExecuteAsync(
        MergeIntentsCommand command,
        CancellationToken cancellationToken) =>
        _operations.MergeIntentsAsync(command, _clock.UtcNow, cancellationToken);

    async Task<AppResult<IReadOnlyList<IntentSummaryDto>>> IListIntents.ExecuteAsync(
        CancellationToken cancellationToken)
    {
        AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
        if (!begin.IsSuccess)
        {
            return AppResult.Failure<IReadOnlyList<IntentSummaryDto>>(begin.Error!);
        }

        await using IAppUnitOfWork unit = begin.Value;
        IReadOnlyList<Intent> intents = await unit.Intents.ListActiveAsync(cancellationToken);
        List<IntentSummaryDto> summaries = [];
        foreach (Intent intent in intents.Where(intent => intent.Id != Intent.ReservedUnsortedId))
        {
            IReadOnlyList<Prompt> prompts =
                await unit.Prompts.ListByIntentAsync(intent.Id, cancellationToken);
            summaries.Add(
                new IntentSummaryDto(
                    intent.Id,
                    intent.CanonicalName,
                    intent.IsArchived,
                    intent.MergedIntoIntentId,
                    prompts.Count,
                    intent.Version));
        }

        await unit.RollbackAsync(cancellationToken);
        return AppResult.Success<IReadOnlyList<IntentSummaryDto>>(
            summaries
                .OrderBy(intent => intent.CanonicalName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
    }

    Task<AppResult<BackupInfoDto>> ICreateBackup.ExecuteAsync(CancellationToken cancellationToken) =>
        _backups.CreateAsync(cancellationToken);

    Task<AppResult> IRestoreBackup.ExecuteAsync(
        RestoreBackupCommand command,
        CancellationToken cancellationToken) =>
        _backups.RestoreAsync(command, cancellationToken);

    Task<AppResult> IRebuildSearchIndex.ExecuteAsync(CancellationToken cancellationToken) =>
        _search.RebuildAsync(cancellationToken);

    public async Task<AppResult<DiagnosticsDto>> ExecuteAsync(CancellationToken cancellationToken)
    {
        AppResult<SearchPageDto<PromptSummaryDto>> database =
            await _search.SearchAsync(new SearchPromptsQuery(string.Empty, 1, 1), cancellationToken);
        AppResult search = await _search.RebuildAsync(cancellationToken);
        AppResult<IReadOnlyList<BackupInfoDto>> backups = await _backups.ListAsync(cancellationToken);
        return AppResult.Success(
            new DiagnosticsDto(
                typeof(PromptApplicationService).Assembly.GetName().Version?.ToString() ?? "development",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                _dataRoot.Resolve().Root,
                database.IsSuccess,
                search.IsSuccess,
                backups.IsSuccess && backups.Value.Count > 0
                    ? backups.Value[0].CreatedAtUtc
                    : null,
                new[] { database.Error, search.Error, backups.Error }.Where(error => error is not null).Cast<AppError>().ToArray()));
    }

    private async Task<AppResult<PromptDetailsDto>> GetDetailsAsync(
        PromptId promptId,
        CancellationToken cancellationToken)
    {
        AppResult<IAppUnitOfWork> begin = await _units.BeginAsync(cancellationToken);
        if (!begin.IsSuccess)
        {
            return AppResult.Failure<PromptDetailsDto>(begin.Error!);
        }

        await using IAppUnitOfWork unit = begin.Value;
        Prompt? prompt = await unit.Prompts.GetAsync(promptId, cancellationToken);
        if (prompt is null)
        {
            return NotFound<PromptDetailsDto>();
        }

        Intent? intent = await unit.Intents.GetAsync(prompt.IntentId, cancellationToken);
        if (intent is null)
        {
            return Persistence<PromptDetailsDto>(
                "prompt.intent_missing",
                new InvalidOperationException("The prompt intent is missing."));
        }

        List<SkillDto> skills = [];
        foreach (PromptSkill relation in prompt.Skills)
        {
            Skill? skill = await unit.Metadata.GetSkillAsync(relation.SkillId, cancellationToken);
            if (skill is not null)
            {
                skills.Add(new SkillDto(
                    skill.Id,
                    skill.Name,
                    relation.Source,
                    relation.Confidence.Value,
                    relation.ExtractorVersion));
            }
        }

        List<EntityDto> entities = [];
        foreach (PromptEntity relation in prompt.Entities)
        {
            Entity? entity = await unit.Metadata.GetEntityAsync(relation.EntityId, cancellationToken);
            if (entity is not null)
            {
                entities.Add(new EntityDto(
                    entity.Id,
                    entity.Name,
                    entity.Type,
                    relation.Source,
                    relation.Confidence.Value,
                    relation.ExtractorVersion));
            }
        }

        List<IntentCandidateDto> candidates = [];
        foreach (IntentCandidateScore candidate in prompt.IntentCandidates)
        {
            Intent? candidateIntent = await unit.Intents.GetAsync(candidate.IntentId, cancellationToken);
            if (candidateIntent is not null)
            {
                candidates.Add(new IntentCandidateDto(
                    candidate.IntentId,
                    candidateIntent.CanonicalName,
                    candidate.Score.Value,
                    candidate.Rank,
                    candidate.ScoringAlgorithmVersion,
                    candidate.CreatedAtUtc,
                    candidateIntent.IsArchived,
                    candidateIntent.MergedIntoIntentId));
            }
        }

        await unit.RollbackAsync(cancellationToken);
        return AppResult.Success(
            new PromptDetailsDto(
                prompt.Id,
                prompt.Body.Value,
                prompt.Title?.Value,
                prompt.TitleSource,
                prompt.CreatedAtUtc,
                prompt.UpdatedAtUtc,
                prompt.LastCopiedAtUtc,
                prompt.CopyCount,
                new IntentSummaryDto(
                    intent.Id,
                    intent.CanonicalName,
                    intent.IsArchived,
                    intent.MergedIntoIntentId),
                prompt.IntentAssignment,
                prompt.IntentReviewState,
                prompt.IntentScoreBasisPoints?.Value,
                prompt.ScoringAlgorithmVersion,
                prompt.MetadataStatus,
                prompt.Version,
                skills,
                entities,
                candidates));
    }

    private async Task<(
        IReadOnlyList<PromptSkill> Skills,
        IReadOnlyList<PromptEntity> Entities,
        IReadOnlyList<SkillDto> SkillDtos,
        IReadOnlyList<EntityDto> EntityDtos)> PersistMetadataAsync(
        IAppUnitOfWork unit,
        PromptId promptId,
        DeterministicMetadata metadata,
        CancellationToken cancellationToken)
    {
        List<PromptSkill> skills = [];
        List<SkillDto> skillDtos = [];
        foreach (ExtractedSkill extracted in metadata.Skills)
        {
            string key = NormalizedText.CreateKey(
                extracted.Name,
                "skill.name.required",
                nameof(extracted.Name));
            Skill? skill = await unit.Metadata.FindSkillByNormalizedKeyAsync(key, cancellationToken);
            if (skill is null)
            {
                skill = Skill.Create(_ids.NewSkillId(), extracted.Name);
                await unit.Metadata.AddSkillAsync(skill, cancellationToken);
            }

            PromptSkill relation = PromptSkill.Create(
                promptId,
                skill.Id,
                MetadataSource.Deterministic,
                extracted.Confidence,
                DeterministicMetadataExtractor.ExtractorVersion);
            skills.Add(relation);
            skillDtos.Add(new SkillDto(
                skill.Id,
                skill.Name,
                relation.Source,
                relation.Confidence.Value,
                relation.ExtractorVersion));
        }

        List<PromptEntity> entities = [];
        List<EntityDto> entityDtos = [];
        foreach (ExtractedEntity extracted in metadata.Entities)
        {
            string key = NormalizedText.CreateKey(
                extracted.Name,
                "entity.name.required",
                nameof(extracted.Name));
            Entity? entity = await unit.Metadata.FindEntityByNormalizedKeyAsync(
                key,
                extracted.Type,
                cancellationToken);
            if (entity is null)
            {
                entity = Entity.Create(_ids.NewEntityId(), extracted.Name, extracted.Type);
                await unit.Metadata.AddEntityAsync(entity, cancellationToken);
            }

            PromptEntity relation = PromptEntity.Create(
                promptId,
                entity.Id,
                MetadataSource.Deterministic,
                extracted.Confidence,
                DeterministicMetadataExtractor.ExtractorVersion);
            entities.Add(relation);
            entityDtos.Add(new EntityDto(
                entity.Id,
                entity.Name,
                entity.Type,
                relation.Source,
                relation.Confidence.Value,
                relation.ExtractorVersion));
        }

        return (skills, entities, skillDtos, entityDtos);
    }

    private static PromptDetailsDto CreateCommittedDetails(
        Prompt prompt,
        IntentResolutionResult resolution,
        IReadOnlyList<SkillDto> skills,
        IReadOnlyList<EntityDto> entities,
        IReadOnlyList<Intent> activeIntents)
    {
        Dictionary<IntentId, Intent> intents = activeIntents
            .Append(resolution.SelectedIntent)
            .DistinctBy(intent => intent.Id)
            .ToDictionary(intent => intent.Id);
        IReadOnlyList<IntentCandidateDto> candidates = resolution.ReviewCandidates
            .Where(candidate => intents.ContainsKey(candidate.IntentId))
            .Select(candidate =>
            {
                Intent intent = intents[candidate.IntentId];
                return new IntentCandidateDto(
                    candidate.IntentId,
                    intent.CanonicalName,
                    candidate.Score.Value,
                    candidate.Rank,
                    candidate.ScoringAlgorithmVersion,
                    candidate.CreatedAtUtc,
                    intent.IsArchived,
                    intent.MergedIntoIntentId);
            })
            .ToArray();

        return new PromptDetailsDto(
            prompt.Id,
            prompt.Body.Value,
            prompt.Title?.Value,
            prompt.TitleSource,
            prompt.CreatedAtUtc,
            prompt.UpdatedAtUtc,
            prompt.LastCopiedAtUtc,
            prompt.CopyCount,
            new IntentSummaryDto(
                resolution.SelectedIntent.Id,
                resolution.SelectedIntent.CanonicalName,
                resolution.SelectedIntent.IsArchived,
                resolution.SelectedIntent.MergedIntoIntentId),
            prompt.IntentAssignment,
            prompt.IntentReviewState,
            prompt.IntentScoreBasisPoints?.Value,
            prompt.ScoringAlgorithmVersion,
            prompt.MetadataStatus,
            prompt.Version,
            skills,
            entities,
            candidates);
    }

    private static AppResult<T> Validation<T>(string message) =>
        AppResult.Failure<T>(new AppError(AppErrorCode.Validation, "validation.failed", message));

    private static AppResult<T> NotFound<T>() =>
        AppResult.Failure<T>(new AppError(AppErrorCode.NotFound, "prompt.not_found", "The prompt was not found."));

    private static AppResult<T> Conflict<T>(string key) =>
        AppResult.Failure<T>(new AppError(AppErrorCode.Conflict, key, "The prompt changed elsewhere."));

    private static AppResult<T> Persistence<T>(string key, Exception exception) =>
        AppResult.Failure<T>(
            new AppError(
                AppErrorCode.PersistenceUnavailable,
                key,
                "The local database operation failed.",
                true,
                new Dictionary<string, string> { ["reason"] = exception.GetType().Name }));
}
