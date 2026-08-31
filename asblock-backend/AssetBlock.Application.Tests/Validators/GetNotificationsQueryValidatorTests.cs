using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Domain.Core.Dto.Notifications;
using AwesomeAssertions;
using FluentValidation.Results;

namespace AssetBlock.Application.Tests.Validators;

public class GetNotificationsQueryValidatorTests
{
    private readonly GetNotificationsQueryValidator _validator = new();

    [Fact]
    public async Task Validate_WhenUnreadOnlyAndSortByReadAt_ShouldFail()
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = true, SortBy = "ReadAt" });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Request");
    }

    [Theory]
    [InlineData("readat")]
    [InlineData("READAT")]
    public async Task Validate_WhenUnreadOnlyAndSortByReadAtCaseInsensitive_ShouldFail(string sortBy)
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = true, SortBy = sortBy });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Validate_WhenUnreadOnlyAndSortByCreatedAt_ShouldPass()
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = true, SortBy = "CreatedAt" });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUnreadOnlyAndSortByEmpty_ShouldPass()
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = true, SortBy = null });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUnreadOnlyAndInvalidSortBy_ShouldFailSortRule()
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = true, SortBy = "NotASort" });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Request.SortBy");
    }

    [Fact]
    public async Task Validate_WhenReadAtSortAndUnreadOnlyFalse_ShouldPass()
    {
        var query = new GetNotificationsQuery(
            Guid.NewGuid(),
            new GetNotificationsRequest { UnreadOnly = false, SortBy = "ReadAt" });

        ValidationResult result = await _validator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }
}
