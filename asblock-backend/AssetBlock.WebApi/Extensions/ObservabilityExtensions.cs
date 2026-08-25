using AssetBlock.Infrastructure.Observability;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Npgsql;

namespace AssetBlock.WebApi.Extensions;

public static class ObservabilityExtensions
{
    public static IServiceCollection AddAssetBlockObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(ObservabilityOptions.SECTION_NAME).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        if (!options.Enabled)
        {
            return services;
        }

        var instanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")
            ?? Environment.MachineName;

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    serviceInstanceId: instanceId)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = environment.EnvironmentName
                }));

        if (options.ExportTraces)
        {
            otel.WithTracing(tracing =>
            {
                tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)))
                    .AddAspNetCoreInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                    })
                    .AddHttpClientInstrumentation(opts =>
                    {
                        opts.RecordException = true;
                    })
                    .AddNpgsql()
                    .AddSource(AssetBlockDiagnostics.ACTIVITY_SOURCE_NAME)
                    .AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(options.OtlpEndpoint);
                    });
            });
        }

        if (options.ExportMetrics)
        {
            otel.WithMetrics(metrics =>
            {
                metrics.AddRuntimeInstrumentation()
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(AssetBlockDiagnostics.METER_NAME)
                    .AddOtlpExporter(opts =>
                    {
                        opts.Endpoint = new Uri(options.OtlpEndpoint);
                    });
            });
        }

        return services;
    }

    public static ILoggingBuilder AddAssetBlockOpenTelemetryLogging(
        this ILoggingBuilder logging,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var options = configuration.GetSection(ObservabilityOptions.SECTION_NAME).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        if (!options.Enabled || !options.ExportLogs)
        {
            return logging;
        }

        var instanceId = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID")
            ?? Environment.MachineName;

        logging.AddOpenTelemetry(otelOpts =>
        {
            var resourceBuilder = ResourceBuilder.CreateDefault()
                .AddService(
                    serviceName: options.ServiceName,
                    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    serviceInstanceId: instanceId)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = environment.EnvironmentName
                });

            otelOpts.SetResourceBuilder(resourceBuilder);
            otelOpts.IncludeFormattedMessage = true;
            otelOpts.IncludeScopes = true;
            otelOpts.AddOtlpExporter(exporterOpts =>
            {
                exporterOpts.Endpoint = new Uri(options.OtlpEndpoint);
            });
        });

        return logging;
    }
}
