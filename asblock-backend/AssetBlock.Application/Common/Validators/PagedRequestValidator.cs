using AssetBlock.Domain.Dto.Paging;
using FluentValidation;

namespace AssetBlock.Application.Common.Validators;

/// <summary>
/// Reusable validation for paging (Page, PageSize). Use via SetValidator in query/command validators.
/// </summary>
internal sealed class PagedRequestValidator : AbstractValidator<PagedRequest>
{
    public PagedRequestValidator()
    {
        RuleFor(r => r.Page)
            .GreaterThanOrEqualTo(PagedRequest.DEFAULT_PAGE)
            .WithMessage($"Page must be at least {PagedRequest.DEFAULT_PAGE}.");
        RuleFor(r => r.PageSize)
            .InclusiveBetween(PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE)
            .WithMessage($"PageSize must be between {PagedRequest.MIN_PAGE_SIZE} and {PagedRequest.MAX_PAGE_SIZE}.");
    }
}
