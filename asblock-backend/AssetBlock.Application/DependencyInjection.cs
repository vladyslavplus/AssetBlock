using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Payments.Checkout;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddApplicationMessaging(assembly);
        services.AddValidatorsFromAssembly(
            assembly,
            filter: null,
            includeInternalTypes: true);
        // Registration order is execution order: logging, then validation, then handler.
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddScoped<CheckoutSessionOrchestrator>();
        services.AddScoped<CheckoutAttributionNormalizer>();
        services.AddScoped<ICheckoutCompletionService, HandleStripeWebhookCommandHandler>();
        services.AddSingleton<ITransactionalEmailComposer, TransactionalEmailComposer>();
        services.AddSingleton(sp => (TransactionalEmailComposer)sp.GetRequiredService<ITransactionalEmailComposer>());
        services.AddSingleton<ITypedCache, JsonTypedCache>();
        return services;
    }
}
