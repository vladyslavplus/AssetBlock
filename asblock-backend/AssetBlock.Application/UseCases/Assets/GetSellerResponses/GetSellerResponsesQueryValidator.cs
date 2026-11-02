using FluentValidation;
namespace AssetBlock.Application.UseCases.Assets.GetSellerResponses;

internal sealed class GetSellerResponsesQueryValidator : AbstractValidator<GetSellerResponsesQuery>
{
    public GetSellerResponsesQueryValidator()
    {
        RuleFor(c => c.OwnerId).NotEmpty();
        RuleFor(c => c.AssetId).NotEmpty();
        RuleFor(c => c.AssetVersionId).NotEmpty();
        RuleFor(c => c.Page).InclusiveBetween(1, 10000);
    }
}
