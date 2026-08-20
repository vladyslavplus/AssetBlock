using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Messaging;

internal sealed class Sender(IServiceProvider serviceProvider) : ISender
{
    private static readonly ConcurrentDictionary<(Type RequestType, Type ResponseType), object> _dispatchers = new();

    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var dispatcher = (Func<IServiceProvider, object, CancellationToken, Task<TResponse>>)_dispatchers.GetOrAdd(
            (request.GetType(), typeof(TResponse)),
            static key => CreateDispatcher<TResponse>(key.RequestType));

        return dispatcher(serviceProvider, request, cancellationToken);
    }

    private static Func<IServiceProvider, object, CancellationToken, Task<TResponse>> CreateDispatcher<TResponse>(
        Type requestType)
    {
        var method = typeof(Sender).GetMethod(nameof(Dispatch), BindingFlags.NonPublic | BindingFlags.Static);
        if (method is null)
        {
            throw new InvalidOperationException("Sender dispatch method was not found.");
        }

        return method
            .MakeGenericMethod(requestType, typeof(TResponse))
            .CreateDelegate<Func<IServiceProvider, object, CancellationToken, Task<TResponse>>>();
    }

    private static Task<TResponse> Dispatch<TRequest, TResponse>(
        IServiceProvider services,
        object request,
        CancellationToken cancellationToken)
        where TRequest : IRequest<TResponse>
    {
        var typedRequest = (TRequest)request;
        RequestHandlerDelegate<TResponse> pipeline = ct =>
        {
            var handlers = services.GetServices<IRequestHandler<TRequest, TResponse>>().ToArray();
            if (handlers.Length == 0)
            {
                throw new InvalidOperationException(
                    $"No handler registered for {typeof(TRequest).FullName}.");
            }

            if (handlers.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Multiple handlers registered for {typeof(TRequest).FullName}.");
            }

            return handlers[0].Handle(typedRequest, ct);
        };

        var behaviors = services.GetServices<IPipelineBehavior<TRequest, TResponse>>().ToArray();
        for (var i = behaviors.Length - 1; i >= 0; i--)
        {
            var behavior = behaviors[i];
            var next = pipeline;
            pipeline = ct => behavior.Handle(typedRequest, next, ct);
        }

        return pipeline(cancellationToken);
    }
}
