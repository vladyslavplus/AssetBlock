using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.Messaging;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Tests.Messaging;

public class SenderTests
{
    [Fact]
    public async Task Send_WhenHandlerIsRegistered_ShouldDispatchToMatchingHandler()
    {
        var pingHandler = new RecordingHandler<Ping, string>(request => request.Value);
        var otherHandler = new RecordingHandler<OtherPing, string>(_ => "other");
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(pingHandler);
            services.AddSingleton<IRequestHandler<OtherPing, string>>(otherHandler);
        });

        var result = await sender.Send(new Ping("ok"));

        result.Should().Be("ok");
        pingHandler.Calls.Should().Be(1);
        otherHandler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenHandlerSucceeds_ShouldReturnResponse()
    {
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>(request => request.Value.ToUpperInvariant()));
        });

        var result = await sender.Send(new Ping("ping"));

        result.Should().Be("PING");
    }

    [Fact]
    public async Task Send_WhenBehaviorsAreRegistered_ShouldRunInRegistrationOrderThenHandler()
    {
        var trace = new List<string>();
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>(_ =>
                {
                    trace.Add("handler");
                    return "done";
                }));
            services.AddSingleton<IPipelineBehavior<Ping, string>>(new TraceBehavior<Ping, string>("logging", trace));
            services.AddSingleton<IPipelineBehavior<Ping, string>>(new TraceBehavior<Ping, string>("validation", trace));
        });

        var result = await sender.Send(new Ping("ok"));

        result.Should().Be("done");
        trace.Should().Equal(
            "logging:before",
            "validation:before",
            "handler",
            "validation:after",
            "logging:after");
    }

    [Fact]
    public async Task Send_WhenValidationFails_ShouldShortCircuitHandler()
    {
        var handler = new RecordingHandler<Ping, string>(_ => "should-not-run");
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(handler);
            services.AddSingleton<IValidator<Ping>, FailingPingValidator>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });

        var act = () => sender.Send(new Ping("ok"));

        await act.Should().ThrowAsync<ValidationException>();
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenValidationFails_ShouldNotResolveHandler()
    {
        var factoryCalls = 0;
        var sender = CreateSender(services =>
        {
            services.AddTransient<IRequestHandler<Ping, string>>(_ =>
            {
                factoryCalls++;
                return new RecordingHandler<Ping, string>(_ => "should-not-run");
            });
            services.AddSingleton<IValidator<Ping>, FailingPingValidator>();
            services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });

        var act = () => sender.Send(new Ping("ok"));

        await act.Should().ThrowAsync<ValidationException>();
        factoryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Send_WhenHandlerThrows_ShouldPropagateException()
    {
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>(_ => throw new InvalidOperationException("boom")));
        });

        var act = () => sender.Send(new Ping("ok"));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public async Task Send_WhenCancellationRequested_ShouldPassTokenToHandler()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        CancellationToken observed = CancellationToken.None;
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>((_, cancellationToken) =>
                {
                    observed = cancellationToken;
                    cancellationToken.ThrowIfCancellationRequested();
                    return "ok";
                }));
        });

        var act = () => sender.Send(new Ping("ok"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        observed.Should().Be(cts.Token);
        observed.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Send_WhenHandlerIsMissing_ShouldThrow()
    {
        var sender = CreateSender(_ => { });

        var act = () => sender.Send(new Ping("ok"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{typeof(Ping).FullName}*");
    }

    [Fact]
    public async Task Send_WhenDuplicateHandlersAreRegistered_ShouldThrow()
    {
        var sender = CreateSender(services =>
        {
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>(_ => "first"));
            services.AddSingleton<IRequestHandler<Ping, string>>(
                new RecordingHandler<Ping, string>(_ => "second"));
        });

        var act = () => sender.Send(new Ping("ok"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{typeof(Ping).FullName}*");
    }

    private static ISender CreateSender(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddTransient<ISender, Sender>();
        configure(services);
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<ISender>();
    }

    private sealed record Ping(string Value) : IRequest<string>;

    private sealed record OtherPing(string Value) : IRequest<string>;

    private sealed class FailingPingValidator : AbstractValidator<Ping>
    {
        public FailingPingValidator()
        {
            RuleFor(x => x.Value).Must(_ => false).WithMessage("invalid");
        }
    }

    private sealed class TraceBehavior<TRequest, TResponse>(string name, IList<string> trace)
        : IPipelineBehavior<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public async Task<TResponse> Handle(
            TRequest request,
            RequestHandlerDelegate<TResponse> next,
            CancellationToken cancellationToken)
        {
            trace.Add($"{name}:before");
            var response = await next(cancellationToken);
            trace.Add($"{name}:after");
            return response;
        }
    }

    private sealed class RecordingHandler<TRequest, TResponse>(Func<TRequest, CancellationToken, TResponse> handler)
        : IRequestHandler<TRequest, TResponse>
        where TRequest : IRequest<TResponse>
    {
        public RecordingHandler(Func<TRequest, TResponse> handler)
            : this((request, _) => handler(request))
        {
        }

        public int Calls { get; private set; }

        public Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(handler(request, cancellationToken));
        }
    }
}
