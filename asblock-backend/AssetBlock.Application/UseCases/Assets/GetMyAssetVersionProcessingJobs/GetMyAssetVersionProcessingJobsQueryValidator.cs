using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;

public sealed class GetMyAssetVersionProcessingJobsQueryValidator : AbstractValidator<GetMyAssetVersionProcessingJobsQuery>
{
    public GetMyAssetVersionProcessingJobsQueryValidator()
    {
        RuleFor(x => x.AssetVersionId)
            .NotEmpty()
            .WithMessage("AssetVersionId is required.");

        RuleFor(x => x.OwnerUserId)
            .NotEmpty()
            .WithMessage("OwnerUserId is required.");
    }
}
