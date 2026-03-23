using Ardalis.Result;
using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Domain.Core.Primitives.Api;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Application.Tests.Behaviors;

public class LoggingBehaviorTests
{
    [Fact]
    public async Task Handle_WhenNextSucceeds_ShouldReturnResponse()
    {
        var logger = NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>.Instance;
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);

        var expected = Result.Success(new TokensResponse("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var result = await behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_WhenNextThrows_ShouldRethrow()
    {
        var logger = NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>.Instance;
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);

        var act = () => behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromException<Result<TokensResponse>>(new InvalidOperationException("boom")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }
}
