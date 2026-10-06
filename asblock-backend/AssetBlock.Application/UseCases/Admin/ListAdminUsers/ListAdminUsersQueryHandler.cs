using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Admin.ListAdminUsers;

internal sealed class ListAdminUsersQueryHandler(IUserStore userStore)
    : IRequestHandler<ListAdminUsersQuery, Result<Domain.Core.Dto.Paging.PagedResult<AdminUserListItem>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AdminUserListItem>>> Handle(
        ListAdminUsersQuery request,
        CancellationToken cancellationToken)
    {
        Domain.Core.Dto.Paging.PagedResult<AdminUserListItem> page = await userStore.ListForAdmin(
            request.Search,
            request.Page,
            request.PageSize,
            cancellationToken);
        return Result.Success(page);
    }
}
