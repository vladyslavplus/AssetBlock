using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Audit;
using MediatR;

namespace AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;

public sealed record GetAuditLogsQuery(GetAuditLogsRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AuditLogListItem>>>;
