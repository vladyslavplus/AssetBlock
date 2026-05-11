using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.ListMyPurchases;

internal sealed class GetMyPurchasesQueryHandler(
    IPurchaseStore purchaseStore,
    ILogger<GetMyPurchasesQueryHandler> logger)
    : IRequestHandler<GetMyPurchasesQuery, Result<Domain.Core.Dto.Paging.PagedResult<PurchaseLibraryItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<PurchaseLibraryItemDto>>> Handle(GetMyPurchasesQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var paged = await purchaseStore.ListForUser(request.UserId, request.Request, cancellationToken);
            logger.LogDebug(
                "Listed {Count} purchases for user {UserId} (page {Page})",
                paged.Items.Count,
                request.UserId,
                paged.Page);
            return Result.Success(paged);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list purchases for user {UserId}", request.UserId);
            return ResultError.Error<Domain.Core.Dto.Paging.PagedResult<PurchaseLibraryItemDto>>(ErrorCodes.ERR_PURCHASES_LIST_FAILED);
        }
    }
}
