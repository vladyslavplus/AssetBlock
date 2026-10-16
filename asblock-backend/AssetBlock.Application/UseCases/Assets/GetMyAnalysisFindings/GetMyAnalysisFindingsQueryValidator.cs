using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetMyAnalysisFindings;

public sealed class GetMyAnalysisFindingsQueryValidator : AbstractValidator<GetMyAnalysisFindingsQuery>
{
    public GetMyAnalysisFindingsQueryValidator()
    {
        RuleFor(x => x.AssetVersionId).NotEmpty();
        RuleFor(x => x.ReportId).NotEmpty();
        RuleFor(x => x.OwnerId).NotEmpty();
        RuleFor(x => x.Page).InclusiveBetween(1, 1_000_000);
    }
}
