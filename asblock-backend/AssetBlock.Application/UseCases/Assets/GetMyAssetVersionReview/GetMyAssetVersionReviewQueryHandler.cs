using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionReview;

internal sealed class GetMyAssetVersionReviewQueryHandler(
    IModerationFoundationStore moderationFoundationStore,
    ICodeAnalysisSellerStore analysisStore)
    : IRequestHandler<GetMyAssetVersionReviewQuery, Result<SellerVersionReviewDto>>
{
    public async Task<Result<SellerVersionReviewDto>> Handle(GetMyAssetVersionReviewQuery request, CancellationToken cancellationToken)
    {
        SellerVersionReviewDto? review = await moderationFoundationStore.GetOwnerVersionReview(
            request.AssetVersionId,
            request.OwnerId,
            cancellationToken);

        if (review is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND);
        }

        SellerAnalysisSummaryDto? analysis = await analysisStore.GetOwnerSummary(
            request.AssetVersionId, request.OwnerId, cancellationToken);
        if (analysis is not null)
        {
            review = review with
            {
                AnalysisDetails = analysis,
                Analysis = analysis.ReportIsCurrent
                    ? SellerAnalysisAvailability.ANALYSIS_AVAILABLE
                    : SellerAnalysisAvailability.ANALYSIS_NOT_AVAILABLE,
                BlockedReasons = analysis.ReportIsCurrent
                    ? review.BlockedReasons
                        .Where(code => code != ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE)
                        .Append(ErrorCodes.ERR_RESEARCH_EVALUATION_PENDING)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : review.BlockedReasons
            };
        }

        return Result.Success(review);
    }
}
