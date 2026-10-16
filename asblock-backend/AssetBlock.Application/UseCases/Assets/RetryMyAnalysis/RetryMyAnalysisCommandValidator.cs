using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.RetryMyAnalysis;

public sealed class RetryMyAnalysisCommandValidator : AbstractValidator<RetryMyAnalysisCommand>
{
    public RetryMyAnalysisCommandValidator()
    {
        RuleFor(x => x.AssetId).NotEmpty();
        RuleFor(x => x.AssetVersionId).NotEmpty();
        RuleFor(x => x.OwnerId).NotEmpty();
        RuleFor(x => x.Request).NotNull();
        RuleFor(x => x.Request.OperationId).NotEmpty();
        RuleFor(x => x.Request.ExpectedRunId).NotEmpty();
        RuleFor(x => x.Request.ExpectedWorkspaceRevision).GreaterThan(0);
        RuleFor(x => x.Request.ExpectedInputDigest).NotEmpty().Length(64).Matches("\\A[0-9a-f]{64}\\z");
        RuleFor(x => x.Request.ExpectedPolicyDigest).NotEmpty().Length(64).Matches("\\A[0-9a-f]{64}\\z");
    }
}
