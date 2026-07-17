using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Audit;
using MediatR;

namespace AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;

internal sealed class GetAuditLogsQueryHandler(
    IAuditStore auditStore) : IRequestHandler<GetAuditLogsQuery, Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>> Handle(GetAuditLogsQuery request, CancellationToken cancellationToken)
    {
        var result = await auditStore.GetPaged(request.Request, cancellationToken);
        return Result.Success(result);
    }
}
