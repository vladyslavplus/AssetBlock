using AssetBlock.Domain.Core.Dto.Assets;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class SimilarAssetsExplanationsTests
{
    [Fact]
    public void Build_WhenPersonalClick_ShouldUseRecommendationChoices()
    {
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            Guid.NewGuid(),
            new SimilarCandidateEvidence(true, true, true, 3));

        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.PERSONAL_RECOMMENDATION_CHOICES);
        explanation.Text.Should().Be("Based on your recommendation choices.");
    }

    [Fact]
    public void Build_WhenPersonalTagScoreWithoutClick_ShouldUseTagInterests()
    {
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            Guid.NewGuid(),
            new SimilarCandidateEvidence(false, true, true, 2));

        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.PERSONAL_TAG_INTERESTS);
        explanation.Text.Should().Be("Matches tags from assets you purchased or reviewed.");
    }

    [Fact]
    public void Build_WhenPopularitySignalWithoutPersonal_ShouldUsePopularInCategory()
    {
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            Guid.NewGuid(),
            new SimilarCandidateEvidence(false, false, true, 4));

        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.POPULAR_IN_CATEGORY);
        explanation.Text.Should().Be("Popular in this category.");
    }

    [Fact]
    public void Build_WhenSharedTagsWithoutStrongerSignals_ShouldUseSharedTagsWithCount()
    {
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            Guid.NewGuid(),
            new SimilarCandidateEvidence(false, false, false, 3));

        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.SHARED_TAGS);
        explanation.Text.Should().Be("Shares 3 tags with this asset.");
    }

    [Fact]
    public void Build_WhenSingleSharedTag_ShouldUseSingularTemplate()
    {
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            Guid.NewGuid(),
            new SimilarCandidateEvidence(false, false, false, 1));

        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.SHARED_TAGS);
        explanation.Text.Should().Be("Shares 1 tag with this asset.");
    }

    [Fact]
    public void Build_WhenNoSignals_ShouldFallBackToSameCategory()
    {
        var assetId = Guid.NewGuid();
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            assetId,
            SimilarAssetsExplanations.Fallback());

        explanation.AssetId.Should().Be(assetId);
        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.SAME_CATEGORY);
        explanation.Text.Should().Be("Similar asset in the same category.");
    }

    [Fact]
    public void Build_ShouldNeverInterpolateUntrustedContent()
    {
        // Templates are fixed strings; only the shared-tag count (an int) varies.
        // No title, description, tag name, score, count, vector, or history reference
        // may appear in any explanation text.
        var codes = new[]
        {
            SimilarAssetsExplanationCodes.PERSONAL_RECOMMENDATION_CHOICES,
            SimilarAssetsExplanationCodes.PERSONAL_TAG_INTERESTS,
            SimilarAssetsExplanationCodes.POPULAR_IN_CATEGORY,
            SimilarAssetsExplanationCodes.SHARED_TAGS,
            SimilarAssetsExplanationCodes.SAME_CATEGORY,
        };

        codes.Should().OnlyHaveUniqueItems();
        foreach (var code in codes)
        {
            code.Should().MatchRegex("^[A-Z_]+$");
        }
    }
}
