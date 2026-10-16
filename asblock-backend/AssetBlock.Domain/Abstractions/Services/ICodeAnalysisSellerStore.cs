using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Owner-authorized analysis projections and recovery for seller version workflows.</summary>
public interface ICodeAnalysisSellerStore
{
    Task<SellerAnalysisSummaryDto?> GetOwnerSummary(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<SellerAnalysisFindingsPageDto?> GetOwnerFindings(
        Guid assetVersionId,
        Guid reportId,
        Guid ownerUserId,
        int page,
        CancellationToken cancellationToken = default);

    Task<SellerAnalysisRetryStoreResult> Retry(
        Guid assetId,
        Guid assetVersionId,
        Guid ownerUserId,
        SellerAnalysisRetryRequest request,
        CancellationToken cancellationToken = default);
}
