using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Moderation.RequestAppeal;

public sealed record RequestAppealCommand(Guid OwnerId, Guid AssetId, Guid AssetVersionId, Guid WorkspaceId,
    long ExpectedWorkspaceRevision, long ExpectedCaseRevision, Guid OperationId, string? Rationale) : IRequest<Result<Guid>>;

internal sealed class RequestAppealCommandValidator : AbstractValidator<RequestAppealCommand>
{
    public RequestAppealCommandValidator()
    {
        RuleFor(x => x.OwnerId).NotEmpty();
        RuleFor(x => x.AssetId).NotEmpty();
        RuleFor(x => x.AssetVersionId).NotEmpty();
        RuleFor(x => x.WorkspaceId).NotEmpty();
        RuleFor(x => x.OperationId).NotEmpty();
        RuleFor(x => x.ExpectedWorkspaceRevision).GreaterThan(0);
        RuleFor(x => x.ExpectedCaseRevision).GreaterThan(0);
        RuleFor(x => x.Rationale).Cascade(CascadeMode.Stop).NotNull().WithMessage("Appeal rationale is required.")
            .Must(x => x!.Length <= 2 * SellerEvidenceLimits.TEXT_MAX_LENGTH).WithMessage("Appeal rationale is too long.")
            .Must(x => !string.IsNullOrWhiteSpace(x)).WithMessage("Appeal rationale is required.")
            .Must(x => x!.Trim().Length <= SellerEvidenceLimits.TEXT_MAX_LENGTH).WithMessage("Appeal rationale is too long.")
            .Must(x => !x!.Contains('\0')).WithMessage("Appeal rationale contains an invalid character.");
    }
}
