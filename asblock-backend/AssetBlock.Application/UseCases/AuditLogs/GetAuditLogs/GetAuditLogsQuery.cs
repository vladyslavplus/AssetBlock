using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Audit;

namespace AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;

public sealed record GetAuditLogsQuery(GetAuditLogsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>>;
