using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionReview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.Assets;

public sealed class GetMyAssetVersionReviewAppealTests
{
    [Theory]
    [InlineData(ErrorCodes.ERR_MODERATION_APPEAL_REQUIRED)]
    [InlineData(ErrorCodes.ERR_MODERATION_REJECTION_FINAL)]
    public async Task Handle_WhenRejectionBlocksSubmission_ShouldSuppressBothSubmitHints(string reason)
    {
        IModerationFoundationStore store = Substitute.For<IModerationFoundationStore>();
        ICodeAnalysisSellerStore analysisStore = Substitute.For<ICodeAnalysisSellerStore>();
        var owner = Guid.NewGuid();
        var asset = Guid.NewGuid();
        var version = Guid.NewGuid();
        var review = new SellerVersionReviewDto(asset, version, 1, AssetVersionProcessingStatus.READY, null, null,
            SellerAnalysisAvailability.ANALYSIS_AVAILABLE, ModerationSubmissionState.REJECTED, false, true, false, [reason]);
        var analysis = new SellerAnalysisSummaryDto(asset, version, SellerAnalysisLifecycle.COMPLETED, Guid.NewGuid(), Guid.NewGuid(), true,
            CodeAnalysisCoverageLevel.FULL, 1, 0, 0, true, null, null, null, null, null, false, null, true, true, "Selected files", [], []);
        store.GetOwnerVersionReview(version, owner, Arg.Any<CancellationToken>()).Returns(review);
        analysisStore.GetOwnerSummary(version, owner, Arg.Any<CancellationToken>()).Returns(analysis);
        Ardalis.Result.Result<SellerVersionReviewDto> result = await new GetMyAssetVersionReviewQueryHandler(store, analysisStore).Handle(new(version, owner), CancellationToken.None);
        result.Value.CanSubmit.Should().BeFalse();
        result.Value.AnalysisDetails!.CanSubmit.Should().BeFalse();
        result.Value.AnalysisDetails.SubmissionBlockedReason.Should().Be(reason);
        result.Value.BlockedReasons.Should().Contain(reason);
    }
}
