using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.GetSimilarAssets;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetSimilarAssetsQueryHandlerTests
{
    private readonly IAssetStore _assetStore = Substitute.For<IAssetStore>();
    private readonly IVectorSearchCapability _vectorCapability = Substitute.For<IVectorSearchCapability>();
    private readonly ITextEmbeddingGenerator _embeddingGenerator = Substitute.For<ITextEmbeddingGenerator>();
    private readonly IRecommendationExposureSigner _exposureSigner = Substitute.For<IRecommendationExposureSigner>();
    private static readonly string _validToken = new('a', RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH);

    public GetSimilarAssetsQueryHandlerTests()
    {
        _exposureSigner.TryCreateToken(Arg.Any<RecommendationExposurePayload>()).Returns(_validToken);
    }

    [Fact]
    public async Task Handle_WhenSourceNotPublic_ShouldReturnNotFound()
    {
        var assetId = Guid.NewGuid();
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns((SimilarPublicAssetsResult?)null);

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.Status.Should().Be(ResultStatus.NotFound);
        result.Errors.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
        await _vectorCapability.DidNotReceive().CheckCapability(Arg.Any<CancellationToken>());
        await _embeddingGenerator.DidNotReceive().CheckModelAvailability(Arg.Any<CancellationToken>());
        _exposureSigner.DidNotReceive().TryCreateToken(Arg.Any<RecommendationExposurePayload>());
    }

    [Fact]
    public async Task Handle_WhenVisibleSourceHasNoCandidates_ShouldReturnEmptyItemsWithoutExposure()
    {
        var assetId = Guid.NewGuid();
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().BeEmpty();
        result.Value.Exposure.Should().BeNull();
        await _embeddingGenerator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
        _exposureSigner.DidNotReceive().TryCreateToken(Arg.Any<RecommendationExposurePayload>());
    }

    [Fact]
    public async Task Handle_WhenEmbeddingsDisabled_ShouldRequestMetadataOnlyRankingAndIssueExposure()
    {
        var assetId = Guid.NewGuid();
        AssetListItem item = CreateListItem(assetId);
        SimilarAssetsQueryOptions? captured = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle().Which.Id.Should().Be(item.Id);
        result.Value.Exposure.Should().NotBeNull();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA);
        result.Value.Exposure.Token.Should().Be(_validToken);
        captured.Should().Be(SimilarAssetsQueryOptions.MetadataOnly);
        await _vectorCapability.DidNotReceive().CheckCapability(Arg.Any<CancellationToken>());
        await _embeddingGenerator.DidNotReceive().CheckModelAvailability(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenLocalCapabilityAvailable_ShouldAllowSemanticRefinementWithoutProviderCalls()
    {
        var assetId = Guid.NewGuid();
        const string modelKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        SimilarAssetsQueryOptions? captured = null;
        _vectorCapability.CheckCapability(Arg.Any<CancellationToken>())
            .Returns(VectorSearchCapabilityResult.Available(modelKey));
        _assetStore.GetSimilarPublic(
                assetId,
                8,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([CreateListItem(Guid.NewGuid())], true));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: true, dimension: 768);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetSimilarAssetsQuery(assetId, 8),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_SEMANTIC);
        captured.Should().NotBeNull();
        captured!.AllowSemanticRefinement.Should().BeTrue();
        captured.CanonicalModelKey.Should().Be(modelKey);
        captured.ExpectedDimension.Should().Be(768);
        captured.ContentSchemaVersion.Should().Be("asset-public-metadata-v1");
        await _embeddingGenerator.DidNotReceive().CheckModelAvailability(Arg.Any<CancellationToken>());
        await _embeddingGenerator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCapabilityCheckFails_ShouldFallBackToMetadataWithoutProviderCalls()
    {
        var assetId = Guid.NewGuid();
        SimilarAssetsQueryOptions? captured = null;
        _vectorCapability.CheckCapability(Arg.Any<CancellationToken>())
            .Returns<Task<VectorSearchCapabilityResult>>(_ => throw new InvalidOperationException("db down"));
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: true);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().Be(SimilarAssetsQueryOptions.MetadataOnly);
        await _embeddingGenerator.DidNotReceive().CheckModelAvailability(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenWhitespaceDescription_ShouldNormalizeToNull()
    {
        var assetId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid()) with { Description = "   " };
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.Value.Items[0].Description.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenSignerCannotCreateToken_ShouldReturnItemsWithoutExposure()
    {
        var assetId = Guid.NewGuid();
        _exposureSigner.TryCreateToken(Arg.Any<RecommendationExposurePayload>()).Returns((string?)null);
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([CreateListItem(Guid.NewGuid())], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().ContainSingle();
        result.Value.Exposure.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenPopularityMode_ShouldRequestPopularityRankingAndIssuePopularityExposure()
    {
        var assetId = Guid.NewGuid();
        AssetListItem item = CreateListItem(Guid.NewGuid());
        SimilarAssetsQueryOptions? captured = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([item], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: true);
        Result<SimilarAssetsResult> result = await handler.Handle(
            new GetSimilarAssetsQuery(assetId, 6, SimilarAssetsConstants.MODE_POPULARITY),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Exposure.Should().NotBeNull();
        result.Value.Exposure!.RankingVersion.Should().Be(RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_POPULARITY);
        captured.Should().NotBeNull();
        captured!.UsePopularityRanking.Should().BeTrue();
        captured.AllowSemanticRefinement.Should().BeFalse();
        await _vectorCapability.DidNotReceive().CheckCapability(Arg.Any<CancellationToken>());
        await _embeddingGenerator.DidNotReceive().Generate(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenDefaultMode_ShouldNotRequestPopularityRanking()
    {
        var assetId = Guid.NewGuid();
        SimilarAssetsQueryOptions? captured = null;
        _assetStore.GetSimilarPublic(
                assetId,
                6,
                Arg.Do<SimilarAssetsQueryOptions>(o => captured = o),
                Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        captured.Should().NotBeNull();
        captured!.UsePopularityRanking.Should().BeFalse();
    }

    [Fact]
    public async Task Handle_WhenEvidencePresent_ShouldExplainEachItemInOrder()
    {
        var assetId = Guid.NewGuid();
        AssetListItem popular = CreateListItem(Guid.NewGuid());
        AssetListItem shared = CreateListItem(Guid.NewGuid());
        AssetListItem plain = CreateListItem(Guid.NewGuid());
        Dictionary<Guid, SimilarCandidateEvidence> evidence = new()
        {
            [popular.Id] = new SimilarCandidateEvidence(false, false, true, 0),
            [shared.Id] = new SimilarCandidateEvidence(false, false, false, 2),
        };
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([popular, shared, plain], false, false, evidence));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(i => i.Id).Should().Equal(popular.Id, shared.Id, plain.Id);
        result.Value.Explanations.Select(e => e.AssetId).Should().Equal(popular.Id, shared.Id, plain.Id);
        result.Value.Explanations.Select(e => e.Code).Should().Equal(
            SimilarAssetsExplanationCodes.POPULAR_IN_CATEGORY,
            SimilarAssetsExplanationCodes.SHARED_TAGS,
            SimilarAssetsExplanationCodes.SAME_CATEGORY);
        result.Value.Explanations[1].Text.Should().Be("Shares 2 tags with this asset.");
    }

    [Fact]
    public async Task Handle_WhenEvidenceMissing_ShouldFallBackWithoutChangingOrder()
    {
        var assetId = Guid.NewGuid();
        AssetListItem first = CreateListItem(Guid.NewGuid());
        AssetListItem second = CreateListItem(Guid.NewGuid());
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([first, second], false));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Select(i => i.Id).Should().Equal(first.Id, second.Id);
        result.Value.Explanations.Should().HaveCount(2);
        result.Value.Explanations.Select(e => e.Code).Should().OnlyContain(c => c == SimilarAssetsExplanationCodes.SAME_CATEGORY);
    }

    [Fact]
    public async Task Handle_WhenEmpty_ShouldReturnEmptyExplanations()
    {
        var assetId = Guid.NewGuid();
        _assetStore.GetSimilarPublic(assetId, 6, Arg.Any<SimilarAssetsQueryOptions>(), Arg.Any<CancellationToken>())
            .Returns(new SimilarPublicAssetsResult([], false, false, new Dictionary<Guid, SimilarCandidateEvidence>()));

        GetSimilarAssetsQueryHandler handler = CreateHandler(enabled: false);
        Result<SimilarAssetsResult> result = await handler.Handle(new GetSimilarAssetsQuery(assetId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Explanations.Should().BeEmpty();
    }

    private GetSimilarAssetsQueryHandler CreateHandler(bool enabled, int dimension = 768)
    {
        var options = new EmbeddingOptions
        {
            Enabled = enabled,
            Dimension = dimension,
            ContentSchemaVersion = "asset-public-metadata-v1"
        };

        return new GetSimilarAssetsQueryHandler(
            _assetStore,
            _exposureSigner,
            TimeProvider.System,
            _vectorCapability,
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<GetSimilarAssetsQueryHandler>.Instance);
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
