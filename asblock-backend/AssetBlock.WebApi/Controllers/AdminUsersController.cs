using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Admin.AssignUserRole;
using AssetBlock.Application.UseCases.Admin.ListAdminUsers;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

[ApiController]
[Authorize(Roles = AppRoles.ADMIN)]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
[Produces("application/json")]
public sealed class AdminUsersController(ISender sender) : ControllerBase
{
    [HttpGet(ApiRoutes.Admin.USERS)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        Ardalis.Result.Result<PagedResult<AdminUserListItem>> result =
            await sender.Send(new ListAdminUsersQuery(search, page, pageSize), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    [HttpPatch(ApiRoutes.Admin.USER_ROLE)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignRole(
        Guid id,
        [FromBody] AssignUserRoleRequest body,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid adminUserId))
        {
            return Unauthorized();
        }

        Ardalis.Result.Result<UserRoleAssignmentResult> result = await sender.Send(
            new AssignUserRoleCommand(adminUserId, id, body.Role, body.ExpectedRoleRevision),
            cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }
}
