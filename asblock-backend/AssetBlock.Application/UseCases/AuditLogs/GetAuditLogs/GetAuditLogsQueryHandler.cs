using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Audit;

namespace AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;

internal sealed class GetAuditLogsQueryHandler(
    IAuditStore auditStore) : IRequestHandler<GetAuditLogsQuery, Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>> Handle(GetAuditLogsQuery request, CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<AuditLogListItem> result = await auditStore.GetPaged(request.Request, cancellationToken);
        return Result.Success(result);
    }
}
