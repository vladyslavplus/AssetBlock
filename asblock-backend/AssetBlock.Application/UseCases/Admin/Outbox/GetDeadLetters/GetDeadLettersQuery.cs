using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Outbox;

namespace AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;

public sealed record GetDeadLettersQuery(GetDeadLettersRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<DeadLetterOutboxListItemDto>>>;
