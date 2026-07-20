using AssetBlock.Application.Common.Behaviors;
using AssetBlock.Application.Services;
using AssetBlock.Domain.Abstractions.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(assembly));
        services.AddValidatorsFromAssembly(
            assembly,
            filter: null,
            includeInternalTypes: true);
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(LoggingBehavior<,>));
        services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        services.AddSingleton<ITransactionalEmailComposer, TransactionalEmailComposer>();
        services.AddSingleton(sp => (TransactionalEmailComposer)sp.GetRequiredService<ITransactionalEmailComposer>());
        return services;
    }
}
