using System.Security.Claims;
using Serilog;

namespace AssetBlock.WebApi.Extensions;

internal static class SerilogExtensions
{
    public static IHostBuilder UseSerilogConfiguration(this IHostBuilder host)
    {
        return host.UseSerilog((context, _, config) =>
            config.ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithEnvironmentName()
                .Enrich.WithMachineName());
    }

    public static IApplicationBuilder UseSerilogRequestLoggingConfiguration(this IApplicationBuilder app)
    {
        return app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
            {
                diagnosticContext.Set("RequestHost", httpContext.Request.Host.Value);
                if (httpContext.User.Identity?.IsAuthenticated == true)
                {
                    // ASP.NET Core JWT Bearer maps JWT "sub" claim to ClaimTypes.NameIdentifier when building User.Claims.
                    diagnosticContext.Set("UserId", httpContext.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value);
                }
            };
        });
    }
}
