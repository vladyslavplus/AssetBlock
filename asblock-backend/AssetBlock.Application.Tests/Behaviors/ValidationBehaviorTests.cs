using Ardalis.Result;
using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Domain.Core.Primitives.Api;
using FluentAssertions;
using FluentValidation;

namespace AssetBlock.Application.Tests.Behaviors;

public class ValidationBehaviorTests
{
    [Fact]
    public async Task Handle_WhenNoValidators_ShouldInvokeNext()
    {
        var behavior = new ValidationBehavior<LoginCommand, Result<TokensResponse>>(
            []);

        var request = new LoginCommand("a@b.com", "pwd");
        var expected = Result.Success(new TokensResponse("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var invoked = false;

        var result = await behavior.Handle(
            request,
            _ =>
            {
                invoked = true;
                return Task.FromResult(expected);
            },
            CancellationToken.None);

        invoked.Should().BeTrue();
        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_WhenValidationFails_ShouldThrowValidationException()
    {
        var validator = new InlineValidator<LoginCommand>();
        validator.RuleFor(x => x.Email).Must(_ => false).WithMessage("bad email");

        var behavior = new ValidationBehavior<LoginCommand, Result<TokensResponse>>(
            [validator]);

        var act = () => behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromResult(Result.Success(new TokensResponse("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))),
            CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Handle_WhenValidationSucceeds_ShouldInvokeNext()
    {
        var validator = new InlineValidator<LoginCommand>();
        validator.RuleFor(x => x.Email).NotEmpty();

        var behavior = new ValidationBehavior<LoginCommand, Result<TokensResponse>>(
            [validator]);

        var expected = Result.Success(new TokensResponse("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var result = await behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }
}
