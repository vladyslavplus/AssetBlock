using AssetBlock.Application.Common.Validators;
using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;

internal sealed class SubmitAssetVersionCommandValidator : AbstractValidator<SubmitAssetVersionCommand>
{
    public SubmitAssetVersionCommandValidator()
    {
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.AssetVersionId).NotEmpty();
        RuleFor(c => c.WorkspaceId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
        RuleFor(c => c.ExpectedWorkspaceRevision).GreaterThan(0);
        RuleFor(c => c.ExpectedCaseRevision).GreaterThan(0);
    }
}
