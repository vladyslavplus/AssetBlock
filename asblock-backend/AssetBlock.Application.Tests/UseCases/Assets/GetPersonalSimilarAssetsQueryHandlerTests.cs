using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetPersonalSimilarAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetPersonalSimilarAssetsQueryHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IVectorSearchCapability _vectorCapability = Substitute.For<IVectorSearchCapability>();
    private readonly IRecommendationExposureSigner _exposureSigner = Substitute.For<IRecommendationExposureSigner>();
    private static readonly string _validToken = new('a', RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH);

    public GetPersonalSimilarAssetsQueryHandlerTests()
    {
        _exposureSigner.TryCreateToken(Arg.Any<RecommendationExposurePayload>()).Returns(_validToken);
    }

    [Fact]
    public async Task Handle_WhenSourceNotPublic_ShouldReturnNotFound()
    {
        var assetId = Guid.NewGuid();
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns((SimilarPublicAssetsResult?)null);

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, Guid.NewGuid()),
            CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Handle_WhenPersonalizationApplied_ShouldPassUserIdAndIssuePersonalExposure()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        SimilarAssetsQueryOptions? captured = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false, true));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.PersonalUserId.Should().Be(userId);
        result.Value.Exposure.Should().NotBeNull();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL);
        result.Value.Exposure.Token.Should().Be(_validToken);
    }

    [Fact]
    public async Task Handle_WhenPersonalizationApplied_ShouldBindTokenAudienceToRequestUser()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        RecommendationExposurePayload? capturedPayload = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false, true));
        _exposureSigner.TryCreateToken(Arg.Do<RecommendationExposurePayload>(p => capturedPayload = p))
            .Returns(_validToken);

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        capturedPayload.Should().NotBeNull();
        capturedPayload!.AudienceUserId.Should().Be(userId);
    }

    [Fact]
    public async Task Handle_WhenPersonalizationNotApplied_ShouldIssueAudienceLessFallbackToken()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        RecommendationExposurePayload? capturedPayload = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false));
        _exposureSigner.TryCreateToken(Arg.Do<RecommendationExposurePayload>(p => capturedPayload = p))
            .Returns(_validToken);

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA);
        capturedPayload.Should().NotBeNull();
        capturedPayload!.AudienceUserId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenPersonalizationNotApplied_ShouldFallBackToPhaseAVersion()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Exposure.Should().NotBeNull();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA);
    }

    [Fact]
    public async Task Handle_WhenEmpty_ShouldReturnNoExposure()
    {
        _assetStore.GetSimilarPublic(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([], false, true));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(Guid.NewGuid(), Guid.NewGuid()),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.Exposure.Should().BeNull();
        _exposureSigner.DidNotReceive().TryCreateToken(Arg.Any<RecommendationExposurePayload>());
    }

    [Fact]
    public async Task Handle_WhenPopularityMode_ShouldCombinePopularityWithPersonalUser()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SimilarAssetsQueryOptions? captured = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([CreateListItem(Guid.NewGuid())], false, true));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId, 6, SimilarAssetsConstants.MODE_POPULARITY),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.UsePopularityRanking.Should().BeTrue();
        captured.PersonalUserId.Should().Be(userId);
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_PERSONAL);
    }

    [Fact]
    public async Task Handle_WhenPersonalSignalsPresent_ShouldExplainWithPersonalReasonsFirst()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem clicked = CreateListItem(Guid.NewGuid());
        AssetListItem tagged = CreateListItem(Guid.NewGuid());
        Dictionary<Guid, SimilarCandidateEvidence> evidence = new()
        {
            [clicked.Id] = new SimilarCandidateEvidence(true, true, true, 5),
            [tagged.Id] = new SimilarCandidateEvidence(false, true, true, 1),
        };
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([clicked, tagged], false, true, evidence));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Explanations.Select(e => e.Code).Should().Equal(
            SimilarAssetsExplanationCodes.PERSONAL_RECOMMENDATION_CHOICES,
            SimilarAssetsExplanationCodes.PERSONAL_TAG_INTERESTS);
    }

    [Fact]
    public async Task Handle_WhenPhaseAFallbackFromPersonalRoute_ShouldNotUsePersonalReasons()
    {
        var assetId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        Dictionary<Guid, SimilarCandidateEvidence> evidence = new()
        {
            [item.Id] = new SimilarCandidateEvidence(false, false, true, 2),
        };
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Any<SimilarAssetsQueryOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false, false, evidence));

        GetPersonalSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetPersonalSimilarAssetsQuery(assetId, userId),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA);
        result.Value.Explanations.Should().ContainSingle()
            .Which.Code.Should().Be(SimilarAssetsExplanationCodes.POPULAR_IN_CATEGORY);
    }

    private GetPersonalSimilarAssetsQueryHandler CreateHandler(bool enabled, int dimension = 768)
    {
        var options = new EmbeddingOptions
        {
            Enabled = enabled,
            Dimension = dimension,
            ContentSchemaVersion = "asset-public-metadata-v1"
        };

        return new GetPersonalSimilarAssetsQueryHandler(
            _assetStore,
            _exposureSigner,
            TimeProvider.System,
            _vectorCapability,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<GetPersonalSimilarAssetsQueryHandler>.Instance);
    }

    private static AssetListItem CreateListItem(Guid id) =>
        new(
            id,
            "Similar pack",
            "desc",
            9.99m,
            Guid.NewGuid(),
            "Tools",
            Guid.NewGuid(),
            "author",
            DateTimeOffset.UtcNow,
            ["tag"],
            4.2);
}
