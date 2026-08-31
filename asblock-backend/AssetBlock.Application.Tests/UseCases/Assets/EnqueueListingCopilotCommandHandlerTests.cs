using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class EnqueueListingCopilotCommandHandlerTests
{
    private readonly IListingCopilotStore _copilotStore = Substitute.For<IListingCopilotStore>();
    private readonly IAssetProcessingJobStore _jobStore = Substitute.For<IAssetProcessingJobStore>();
    private readonly IAiGenerationProviderRegistry _registry = Substitute.For<IAiGenerationProviderRegistry>();
    private readonly IAiGenerationProvider _provider = Substitute.For<IAiGenerationProvider>();
    private readonly EnqueueListingCopilotCommandHandler _handler;

    public EnqueueListingCopilotCommandHandlerTests()
    {
        _provider.OrderedModelIds.Returns(["fixture/openrouter-test"]);
        _registry.TryGet(AiProviderKind.OPENROUTER, out Arg.Any<IAiGenerationProvider?>())
            .Returns(x =>
            {
                x[1] = _provider;
                return true;
            });
        _handler = new EnqueueListingCopilotCommandHandler(
            _copilotStore,
            _jobStore,
            _registry,
            Microsoft.Extensions.Options.Options.Create(new AiOptions
            {
                Enabled = true,
                Provider = "OpenRouter",
                PromptPolicyVersion = AiPromptPolicies.LISTING_COPILOT_V1
            }));
    }

    [Fact]
    public async Task Handle_WhenVersionMissing_ShouldReturnNotFound()
    {
        var versionId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        _copilotStore.GetOwnedVersion(versionId, ownerId, Arg.Any<CancellationToken>())
            .Returns((ListingCopilotOwnedVersion?)null);

        Result<ListingCopilotEnqueueResponse> result = await _handler.Handle(new EnqueueListingCopilotCommand(versionId, ownerId), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _jobStore.DidNotReceive().Enqueue(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<int>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<AssetProcessingPayload>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAiDisabled_ShouldNotEnqueue()
    {
        var handler = new EnqueueListingCopilotCommandHandler(
            _copilotStore,
            _jobStore,
            _registry,
            Microsoft.Extensions.Options.Options.Create(new AiOptions { Enabled = false, Provider = "OpenRouter" }));
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.READY, true);
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);

        Result<ListingCopilotEnqueueResponse> result = await handler.Handle(
            new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        result.ValidationErrors.Should().Contain(e => e.Identifier == ErrorCodes.ERR_AI_DISABLED);
        await _jobStore.DidNotReceive().Enqueue(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<int>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<AssetProcessingPayload>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenVersionNotReady_ShouldConflict()
    {
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN, true);
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);

        Result<ListingCopilotEnqueueResponse> result = await _handler.Handle(
            new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_AI_VERSION_NOT_READY);
    }

    [Fact]
    public async Task Handle_WhenAnalysisMissing_ShouldConflict()
    {
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.READY, false);
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);

        Result<ListingCopilotEnqueueResponse> result = await _handler.Handle(
            new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Conflict);
        result.Errors.Should().Contain(ErrorCodes.ERR_AI_ARCHIVE_ANALYSIS_MISSING);
    }

    [Fact]
    public async Task Handle_WhenProviderInvalid_ShouldNotEnqueue()
    {
        var handler = new EnqueueListingCopilotCommandHandler(
            _copilotStore,
            _jobStore,
            Substitute.For<IAiGenerationProviderRegistry>(),
            Microsoft.Extensions.Options.Options.Create(new AiOptions
            {
                Enabled = true,
                Provider = "not-a-provider",
                PromptPolicyVersion = AiPromptPolicies.LISTING_COPILOT_V1
            }));
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.READY, true);
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);

        Result<ListingCopilotEnqueueResponse> result = await handler.Handle(
            new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(ResultStatus.Invalid);
        await _jobStore.DidNotReceive().Enqueue(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<int>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<AssetProcessingPayload>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenEligibleTwice_ShouldReturnSameJobId()
    {
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.READY, true);
        var jobId = Guid.NewGuid();
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);
        _jobStore.Enqueue(
            owned.AssetId,
            owned.AssetVersionId,
            AssetProcessingJobType.LISTING_COPILOT,
            AiPromptPolicies.LISTING_COPILOT_DEFINITION_VERSION,
            TimeSpan.Zero,
            Arg.Any<ListingCopilotPayload>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).Returns(jobId);

        var command = new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid());
        Result<ListingCopilotEnqueueResponse> first = await _handler.Handle(command, CancellationToken.None);
        Result<ListingCopilotEnqueueResponse> second = await _handler.Handle(command, CancellationToken.None);

        first.Value.JobId.Should().Be(jobId);
        second.Value.JobId.Should().Be(jobId);
    }

    [Fact]
    public async Task Handle_WhenCalledConcurrently_ShouldReturnSameJobId()
    {
        ListingCopilotOwnedVersion owned = Owned(AssetVersionProcessingStatus.READY, true);
        var jobId = Guid.NewGuid();
        _copilotStore.GetOwnedVersion(owned.AssetVersionId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(owned);
        _jobStore.Enqueue(
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                AssetProcessingJobType.LISTING_COPILOT,
                Arg.Any<int>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<AssetProcessingPayload>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(jobId);

        var command = new EnqueueListingCopilotCommand(owned.AssetVersionId, Guid.NewGuid());
        Result<ListingCopilotEnqueueResponse>[] results = await Task.WhenAll(
            _handler.Handle(command, CancellationToken.None),
            _handler.Handle(command, CancellationToken.None));

        results.Select(r => r.Value.JobId).Should().OnlyContain(id => id == jobId);
    }

    private static ListingCopilotOwnedVersion Owned(AssetVersionProcessingStatus status, bool hasAnalysis) =>
        new(Guid.NewGuid(), Guid.NewGuid(), status, hasAnalysis, "pack.zip");
}
