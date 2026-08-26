using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;

public sealed class GetMyAssetProcessingJobsQueryValidator : AbstractValidator<GetMyAssetProcessingJobsQuery>
{
    public GetMyAssetProcessingJobsQueryValidator()
    {
        RuleFor(x => x.AssetId)
            .NotEmpty()
            .WithMessage("AssetId is required.");

        RuleFor(x => x.OwnerUserId)
            .NotEmpty()
            .WithMessage("OwnerUserId is required.");
    }
}
