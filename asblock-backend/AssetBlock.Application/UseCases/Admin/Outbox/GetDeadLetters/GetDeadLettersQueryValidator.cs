using FluentValidation;

namespace AssetBlock.Application.UseCases.Admin.Outbox.GetDeadLetters;

internal sealed class GetDeadLettersQueryValidator : AbstractValidator<GetDeadLettersQuery>
{
    public GetDeadLettersQueryValidator()
    {
        RuleFor(q => q.Request).NotNull();
        RuleFor(q => q.Request.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.Request.PageSize).InclusiveBetween(1, 100);
    }
}
