using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;
namespace AssetBlock.Application.UseCases.Assets.GetSellerResponses;

public sealed record GetSellerResponsesQuery(Guid OwnerId, Guid AssetId, Guid AssetVersionId, int Page = 1) : IRequest<Result<SellerResponsesPageDto>>;
