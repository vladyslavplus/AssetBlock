using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;

internal sealed class SaveSellerEvidenceCommandValidator : AbstractValidator<SaveSellerEvidenceCommand>
{
    public SaveSellerEvidenceCommandValidator()
    {
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.AssetVersionId).NotEmpty();
        RuleFor(c => c.WorkspaceId).NotEmpty();
        RuleFor(c => c.ReportId).NotEmpty();
        RuleFor(c => c.OperationId).NotEmpty();
        RuleFor(c => c.ExpectedWorkspaceRevision).GreaterThan(0);
        RuleFor(c => c.ExpectedEvidenceRevision).GreaterThanOrEqualTo(0);
        RuleFor(c => c.Text).NotNull().MaximumLength(SellerEvidenceLimits.TEXT_MAX_LENGTH);
        RuleFor(c => c.Text).Must(text => !text.Contains('\0')).When(c => c.Text is not null).WithMessage("Text contains unsupported characters.");
        RuleFor(c => c).Must(c => c.ResponseType.HasValue == c.FindingId.HasValue && (!c.ResponseType.HasValue || Enum.IsDefined(c.ResponseType.Value)))
            .WithMessage("A response requires a finding and a valid response type.");
        RuleFor(c => c.Text).Must(text => !string.IsNullOrWhiteSpace(text))
            .When(c => c.ResponseType.HasValue && c.ResponseType != SellerResponseType.NO_EVIDENCE)
            .WithMessage("Explain the response or choose No evidence.");
    }
}
