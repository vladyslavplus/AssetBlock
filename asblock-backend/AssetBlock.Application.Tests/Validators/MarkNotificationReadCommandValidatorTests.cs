using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AwesomeAssertions;

namespace AssetBlock.Application.Tests.Validators;

public sealed class MarkNotificationReadCommandValidatorTests
{
    private readonly MarkNotificationReadCommandValidator _validator = new();

    [Fact]
    public async Task Validate_WhenValidCommand_ShouldPass()
    {
        var cmd = new MarkNotificationReadCommand(Guid.NewGuid(), Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validate_WhenUserIdEmpty_ShouldFail()
    {
        var cmd = new MarkNotificationReadCommand(Guid.Empty, Guid.NewGuid());
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.UserId));
    }

    [Fact]
    public async Task Validate_WhenNotificationIdEmpty_ShouldFail()
    {
        var cmd = new MarkNotificationReadCommand(Guid.NewGuid(), Guid.Empty);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(cmd.NotificationId));
    }
}
