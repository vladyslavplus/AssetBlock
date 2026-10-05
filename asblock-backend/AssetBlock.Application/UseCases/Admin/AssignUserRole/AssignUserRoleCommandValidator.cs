using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Admin.AssignUserRole;

internal sealed class AssignUserRoleCommandValidator : AbstractValidator<AssignUserRoleCommand>
{
    public AssignUserRoleCommandValidator()
    {
        RuleFor(c => c.AdminUserId).NotEmpty();
        RuleFor(c => c.TargetUserId).NotEmpty();
        RuleFor(c => c.ExpectedRoleRevision).GreaterThan(0);
        RuleFor(c => c.Role)
            .Must(role => AppRoles.AdminAssignableRoles.Contains(role))
            .WithMessage(ErrorCodes.ERR_USER_ROLE_ASSIGNMENT_FORBIDDEN);
    }
}
