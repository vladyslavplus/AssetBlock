using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class ListingCopilotJobHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IAssetArchiveAnalysisStore _analysisStore = Substitute.For<IAssetArchiveAnalysisStore>();
    private readonly IListingCopilotStore _copilotStore = Substitute.For<IListingCopilotStore>();
    private readonly IListingSuggestionOrchestrator _orchestrator = Substitute.For<IListingSuggestionOrchestrator>();
    private readonly AiOptions _aiOptions = new() { Enabled = true, Provider = "OpenRouter" };
    private readonly OpenRouterOptions _openRouterOptions = new() { ZeroDataRetention = true };
    private readonly ListingCopilotJobHandler _sut;

    public ListingCopilotJobHandlerTests()
    {
        _copilotStore.ListCategoryNames(Arg.Any<CancellationToken>()).Returns(["3D"]);
        _copilotStore.ListTagNames(Arg.Any<CancellationToken>()).Returns(["lowpoly"]);
        _sut = new ListingCopilotJobHandler(
            _assetStore,
            _analysisStore,
            _copilotStore,
            _orchestrator,
            Microsoft.Extensions.Options.Options.Create(_aiOptions),
            Microsoft.Extensions.Options.Options.Create(_openRouterOptions),
            NullLogger<ListingCopilotJobHandler>.Instance);
    }

    [Fact]
    public async Task Process_WhenCancelled_ShouldRethrow()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task<AssetProcessingJobOutcome>> act = async () => await _sut.Process(CreateContext(cts.Token), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await _orchestrator.DidNotReceive().Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenMetadataMalformed_ShouldBeTerminal()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId, manifestJson: "{bad"));

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.TerminalFailure terminal = outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>().Subject;
        terminal.ErrorCode.Should().Be(ErrorCodes.INVALID_JOB_PAYLOAD);
        await _orchestrator.DidNotReceive().Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenAllowlistOverflows_ShouldBeTerminalWithoutCallingProvider()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _copilotStore.ListCategoryNames(Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, ListingSuggestionBounds.MAX_ALLOWLIST_CATEGORIES + 1)
                .Select(i => $"cat-{i}")
                .ToArray());

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.TerminalFailure terminal = outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>().Subject;
        terminal.ErrorCode.Should().Be(ErrorCodes.ERR_AI_ALLOWLIST_OVERFLOW);
        await _orchestrator.DidNotReceive().Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenSuccess_ShouldSendOnlyBoundedMetadataAndCommit()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        ListingSuggestionGenerationRequest? captured = null;
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                captured = ci.Arg<ListingSuggestionGenerationRequest>();
                return SuccessResult();
            });
        _copilotStore.TryCommitSucceeded(
            context.JobId,
            context.LeaseToken,
            context.AssetId,
            context.AssetVersionId,
            Arg.Any<ListingCopilotSuggestionWrite>(),
            Arg.Any<CancellationToken>()).Returns(true);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        captured.Should().NotBeNull();
        captured!.Archive.SampleEntryPaths.Should().BeEmpty();
        captured.Readme!.FileName.Should().Be(ListingSuggestionBounds.README_LABEL);
        captured.Archive.Format.Should().Be("zip");
        captured.AllowedCategories.Should().Equal("3D");
        captured.PromptPolicyVersion.Should().Be(AiPromptPolicies.LISTING_COPILOT_V1);
    }

    [Fact]
    public async Task Process_WhenProviderRetryable_ShouldPreserveRetryAfter()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListingSuggestionResult(
                AiGenerationOutcomeKind.RETRYABLE_FAILURE,
                true,
                null,
                AiProviderKind.OPENROUTER,
                null,
                null,
                null,
                null,
                TimeSpan.FromMilliseconds(5),
                null,
                TimeSpan.FromSeconds(9),
                ErrorCodes.ERR_AI_RATE_LIMITED));

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.RetryableFailure retryable = outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>().Subject;
        retryable.ErrorCode.Should().Be(ErrorCodes.ERR_AI_RATE_LIMITED);
        retryable.RetryAfter.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task Process_WhenLeaseLost_ShouldBeRetryable()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());
        _copilotStore.TryCommitSucceeded(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<ListingCopilotSuggestionWrite>(),
            Arg.Any<CancellationToken>()).Returns(false);

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.RetryableFailure retryable = outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>().Subject;
        retryable.ErrorCode.Should().Be(ErrorCodes.LEASE_LOST);
    }

    [Fact]
    public async Task Process_WhenDisabled_ShouldBeTerminalAiDisabled()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListingSuggestionResult(
                AiGenerationOutcomeKind.DISABLED,
                false,
                null,
                AiProviderKind.OPENROUTER,
                null,
                null,
                null,
                null,
                TimeSpan.Zero,
                null,
                null,
                ErrorCodes.ERR_AI_DISABLED));

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.TerminalFailure terminal = outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>().Subject;
        terminal.ErrorCode.Should().Be(ErrorCodes.ERR_AI_DISABLED);
        await _copilotStore.DidNotReceive().TryCommitSucceeded(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<ListingCopilotSuggestionWrite>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenProviderTerminal_ShouldBeTerminal()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ListingSuggestionResult(
                AiGenerationOutcomeKind.TERMINAL_FAILURE,
                false,
                null,
                AiProviderKind.OPENROUTER,
                null,
                null,
                null,
                null,
                TimeSpan.FromMilliseconds(4),
                null,
                null,
                ErrorCodes.ERR_AI_INVALID_RESPONSE));

        AssetProcessingJobOutcome outcome = await _sut.Process(context, CancellationToken.None);

        AssetProcessingJobOutcome.TerminalFailure terminal = outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>().Subject;
        terminal.ErrorCode.Should().Be(ErrorCodes.ERR_AI_INVALID_RESPONSE);
    }

    private static AssetProcessingJobContext<ListingCopilotPayload> CreateContext(
        CancellationToken cancellationToken = default) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AiPromptPolicies.LISTING_COPILOT_DEFINITION_VERSION,
            1,
            3,
            new ListingCopilotPayload(AiPromptPolicies.LISTING_COPILOT_V1),
            null,
            cancellationToken);

    private static AssetVersion CreateVersion(AssetProcessingJobContext<ListingCopilotPayload> context) =>
        new()
        {
            Id = context.AssetVersionId,
            AssetId = context.AssetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "assets/secret-key.bin",
            FileName = "pack.zip",
            ContentLength = 12,
            ContentSha256 = new string('a', 64),
            ReleaseNotes = "notes",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms",
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow
        };

    private static AssetArchiveAnalysis CreateAnalysis(Guid versionId, string? manifestJson = null) =>
        new()
        {
            AssetVersionId = versionId,
            FileCount = 2,
            TotalExpandedBytes = 40,
            ReadmeContent = "A chair",
            ManifestMetadata = manifestJson,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static ListingSuggestionResult SuccessResult() =>
        new(
            AiGenerationOutcomeKind.SUCCESS,
            false,
            new ListingSuggestion("Chair", "A chair", "3D", ["lowpoly"]),
            AiProviderKind.OPENROUTER,
            "fixture/openrouter-test",
            "TestHost",
            1,
            2,
            TimeSpan.FromMilliseconds(8),
            "gen-1",
            null,
            null);

    [Fact]
    public async Task Process_WhenOpenRouterAndZeroDataRetentionFalse_ShouldOmitReadme()
    {
        IOptions<AiOptions> aiOptions = Microsoft.Extensions.Options.Options.Create(new AiOptions { Enabled = true, Provider = "OpenRouter" });
        IOptions<OpenRouterOptions> openRouterOptions = Microsoft.Extensions.Options.Options.Create(new OpenRouterOptions { ZeroDataRetention = false });
        var sut = new ListingCopilotJobHandler(
            _assetStore,
            _analysisStore,
            _copilotStore,
            _orchestrator,
            aiOptions,
            openRouterOptions,
            NullLogger<ListingCopilotJobHandler>.Instance);

        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateAnalysis(context.AssetVersionId));
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await sut.Process(context, CancellationToken.None);

        await _orchestrator.Received(1).Generate(
            Arg.Is<ListingSuggestionGenerationRequest>(r => r.Readme == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_WhenOpenRouterAndZeroDataRetentionTrue_ShouldIncludeSanitizedReadme()
    {
        AssetProcessingJobContext<ListingCopilotPayload> context = CreateContext();
        _assetStore.GetVersion(context.AssetId, context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(CreateVersion(context));
        _analysisStore.GetByVersionId(context.AssetVersionId, Arg.Any<CancellationToken>())
            .Returns(new AssetArchiveAnalysis
            {
                AssetVersionId = context.AssetVersionId,
                FileCount = 2,
                TotalExpandedBytes = 40,
                ReadmeContent = "# Title\nAPI_KEY=12345\nhttps://malicious.url\nClean description here.",
                CreatedAt = DateTimeOffset.UtcNow
            });
        _orchestrator.Generate(Arg.Any<ListingSuggestionGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(SuccessResult());

        await _sut.Process(context, CancellationToken.None);

        await _orchestrator.Received(1).Generate(
            Arg.Is<ListingSuggestionGenerationRequest>(r =>
                r.Readme != null &&
                !r.Readme.Text.Contains("API_KEY") &&
                !r.Readme.Text.Contains("https://malicious.url") &&
                r.Readme.Text.Contains("Clean description here.")),
            Arg.Any<CancellationToken>());
    }
}
