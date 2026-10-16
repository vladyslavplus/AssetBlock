using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetMyAnalysisFindings;

internal sealed class GetMyAnalysisFindingsQueryHandler(ICodeAnalysisSellerStore store)
    : IRequestHandler<GetMyAnalysisFindingsQuery, Result<SellerAnalysisFindingsPageDto>>
{
    public async Task<Result<SellerAnalysisFindingsPageDto>> Handle(GetMyAnalysisFindingsQuery request, CancellationToken cancellationToken)
    {
        SellerAnalysisFindingsPageDto? page = await store.GetOwnerFindings(
            request.AssetVersionId, request.ReportId, request.OwnerId, request.Page, cancellationToken).ConfigureAwait(false);
        return page is null ? Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND) : Result.Success(page);
    }
}
