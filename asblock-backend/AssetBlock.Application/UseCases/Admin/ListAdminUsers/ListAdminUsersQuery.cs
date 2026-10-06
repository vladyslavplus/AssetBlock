using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Admin.ListAdminUsers;

public sealed record ListAdminUsersQuery(string? Search, int Page, int PageSize)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<AdminUserListItem>>>;
