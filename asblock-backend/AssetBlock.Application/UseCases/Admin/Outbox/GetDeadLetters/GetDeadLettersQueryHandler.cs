using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Outbox;

namespace AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;

internal sealed class GetDeadLettersQueryHandler(
    IOutboxStore outboxStore) : IRequestHandler<GetDeadLettersQuery, Result<Domain.Core.Dto.Paging.PagedResult<DeadLetterOutboxListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<DeadLetterOutboxListItemDto>>> Handle(
        GetDeadLettersQuery request,
        CancellationToken cancellationToken)
    {
        var result = await outboxStore.GetDeadLetters(request.Request, cancellationToken);
        return Result.Success(result);
    }
}
