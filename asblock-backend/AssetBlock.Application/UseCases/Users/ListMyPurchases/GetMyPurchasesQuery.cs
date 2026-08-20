using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Users.ListMyPurchases;

public sealed record GetMyPurchasesQuery(Guid UserId, ListMyPurchasesRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<PurchaseLibraryItemDto>>>;
