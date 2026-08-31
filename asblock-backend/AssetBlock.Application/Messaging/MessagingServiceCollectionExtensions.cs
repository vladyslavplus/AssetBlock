using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Application.Messaging;

internal static class MessagingServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationMessaging(this IServiceCollection services, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assembly);

        services.AddTransient<ISender, Sender>();
        RegisterRequestHandlers(services, assembly);
        return services;
    }

    private static void RegisterRequestHandlers(IServiceCollection services, Assembly assembly)
    {
        Type handlerInterface = typeof(IRequestHandler<,>);

        foreach (Type type in assembly.GetTypes())
        {
            if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters)
            {
                continue;
            }

            foreach (Type implementedInterface in type.GetInterfaces())
            {
                if (!implementedInterface.IsGenericType
                    || implementedInterface.GetGenericTypeDefinition() != handlerInterface)
                {
                    continue;
                }

                services.AddTransient(implementedInterface, type);
            }
        }
    }
}
