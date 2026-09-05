using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Observability;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace AssetBlock.WebApi.Extensions;

public static class ObservabilityExtensions
{
    private static readonly string _processInstanceId = Guid.NewGuid().ToString("N");

    private static string ResolveServiceInstanceId()
    {
        var envInstanceId = Environment.GetEnvironmentVariable("OTEL_SERVICE_INSTANCE_ID");
        return !string.IsNullOrWhiteSpace(envInstanceId) ? envInstanceId.Trim() : _processInstanceId;
    }

    public static IServiceCollection AddAssetBlockObservability(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ObservabilityOptions options = configuration.GetSection(ObservabilityOptions.SECTION_NAME).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        if (!options.Enabled)
        {
            return services;
        }

        var instanceId = ResolveServiceInstanceId();

        OpenTelemetryBuilder otel = services.AddOpenTelemetry()
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
                // Production note: TraceSampleRatio defaults to 1.0 for local/Aspire development.
                // In production/staging deployments, lower TraceSampleRatio (e.g., 0.05-0.10) and ensure
                // sensitive authorization tokens, query secrets, and PII attributes are scrubbed or excluded.
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
        ObservabilityOptions options = configuration.GetSection(ObservabilityOptions.SECTION_NAME).Get<ObservabilityOptions>() ?? new ObservabilityOptions();

        if (!options.Enabled || !options.ExportLogs)
        {
            return logging;
        }

        var instanceId = ResolveServiceInstanceId();

        logging.AddOpenTelemetry(otelOpts =>
        {
            ResourceBuilder resourceBuilder = ResourceBuilder.CreateDefault()
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
            otelOpts.AddProcessor(new Observability.OpenTelemetryLoggingPrivacyProcessor());
            otelOpts.AddOtlpExporter(exporterOpts =>
            {
                exporterOpts.Endpoint = new Uri(options.OtlpEndpoint);
            });
        });

        return logging;
    }
}
