using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Users.ListMyPurchases;

public sealed record GetMyPurchasesQuery(Guid UserId, ListMyPurchasesRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<PurchaseLibraryItemDto>>>;
