using Ardalis.Result;
using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Domain.Core.Primitives.Api;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Application.Tests.Behaviors;

public class LoggingBehaviorTests
{
    [Fact]
    public async Task Handle_WhenNextSucceeds_ShouldReturnResponse()
    {
        NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>> logger = NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>.Instance;
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);

        var expected = Result.Success(new TokensResponse("a", "b", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        Result<TokensResponse> result = await behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromResult(expected),
            CancellationToken.None);

        result.Should().Be(expected);
    }

    [Fact]
    public async Task Handle_WhenNextThrows_ShouldRethrow()
    {
        NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>> logger = NullLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>.Instance;
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);

        Func<Task<Result<TokensResponse>>> act = () => behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromException<Result<TokensResponse>>(new InvalidOperationException("boom")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Handle_WhenRequestCancelled_ShouldLogDebugAndRethrow()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>();
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task<Result<TokensResponse>>> act = () => behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromException<Result<TokensResponse>>(new OperationCanceledException(cts.Token)),
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Exception is OperationCanceledException);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Handle_WhenUnexpectedException_ShouldLogErrorAndRethrow()
    {
        var logger = new RecordingLogger<LoggingBehavior<LoginCommand, Result<TokensResponse>>>();
        var behavior = new LoggingBehavior<LoginCommand, Result<TokensResponse>>(logger);

        Func<Task<Result<TokensResponse>>> act = () => behavior.Handle(
            new LoginCommand("a@b.com", "pwd"),
            _ => Task.FromException<Result<TokensResponse>>(new InvalidOperationException("boom")),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Exception is InvalidOperationException);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception));
        }
    }
}
