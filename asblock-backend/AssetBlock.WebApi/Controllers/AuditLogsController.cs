using AssetBlock.Application.UseCases.AuditLogs.GetAuditLogs;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Admin-only read access to the append-only audit log.
/// Absolute route avoids inheriting api/[controller] from ApiControllerBase.
/// </summary>
[ApiController]
[Route(ApiRoutes.Admin.AUDIT_LOGS)]
[Authorize(Roles = AppRoles.ADMIN)]
[Produces("application/json")]
public sealed class AuditLogsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Get paged audit log entries with optional filters. Sorted by OccurredAt DESC, Id DESC.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Get([FromQuery] GetAuditLogsRequest? request, CancellationToken cancellationToken)
    {
        request ??= new GetAuditLogsRequest();
        var result = await sender.Send(new GetAuditLogsQuery(request), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }
}
