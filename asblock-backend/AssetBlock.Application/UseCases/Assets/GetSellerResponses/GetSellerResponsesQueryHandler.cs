using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
namespace AssetBlock.Application.UseCases.Assets.GetSellerResponses;

internal sealed class GetSellerResponsesQueryHandler(IModerationFoundationStore store) : IRequestHandler<GetSellerResponsesQuery, Result<SellerResponsesPageDto>>
{
    public async Task<Result<SellerResponsesPageDto>> Handle(GetSellerResponsesQuery request, CancellationToken cancellationToken)
    {
        SellerResponsesPageDto? result = await store.GetOwnerResponses(request.AssetId, request.AssetVersionId, request.OwnerId, request.Page, cancellationToken);
        return result is null ? Result.NotFound(ErrorCodes.ERR_ASSET_VERSION_NOT_FOUND) : Result.Success(result);
    }
}
