using FluentValidation;

namespace AssetBlock.Application.UseCases.Admin.ListAdminUsers;

internal sealed class ListAdminUsersQueryValidator : AbstractValidator<ListAdminUsersQuery>
{
    public ListAdminUsersQueryValidator()
    {
        RuleFor(q => q.Page).GreaterThan(0);
        RuleFor(q => q.PageSize).InclusiveBetween(1, 100);
    }
}
