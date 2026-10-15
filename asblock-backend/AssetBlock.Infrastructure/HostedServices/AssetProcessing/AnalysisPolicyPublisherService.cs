using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing;

/// <summary>
/// Publishes the approved analysis policy and runtime manifest this deployment declares, retrying
/// only while the failure is transient, such as a database that is not reachable yet. A transient
/// failure is retried with bounded backoff until publication succeeds or the attempts are exhausted;
/// a permanent failure such as a provider, permission or configuration fault is surfaced immediately
/// and stops publication instead of masquerading as a temporary outage.
/// <para>
/// Publication is idempotent and insert-only, so a retry never edits or duplicates an approved row.
/// An asynchronous background service is not a startup barrier, so nothing here is what opens
/// analysis claims: while this row is absent the readiness gate keeps every new claim closed, and
/// the worker re-evaluates readiness on its claim loop, which is what opens them once the row
/// appears.
/// </para>
/// </summary>
public sealed class AnalysisPolicyPublisherService(
    IServiceScopeFactory scopeFactory,
    IOptions<CodeAnalysisOptions> options,
    TimeProvider timeProvider,
    ILogger<AnalysisPolicyPublisherService> logger) : BackgroundService
{
    private static readonly TimeSpan _initialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _maxRetryDelay = TimeSpan.FromMinutes(2);
    private const int MAX_ATTEMPTS = 12;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CodeAnalysisOptions configuration = options.Value;
        if (!configuration.Enabled)
        {
            logger.LogInformation("Code analysis is disabled by configuration; no policy manifest is published.");
            return;
        }

        AnalysisPolicyManifest manifest = ToManifest(configuration.PolicyManifest);
        TimeSpan retryDelay = _initialRetryDelay;

        for (var attempt = 1; attempt <= MAX_ATTEMPTS; attempt++)
        {
            bool settled;
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                AnalysisPolicyPublishResult result = await scope.ServiceProvider
                    .GetRequiredService<IAnalysisPolicyPublisher>()
                    .Publish(manifest, stoppingToken);

                if (result.Published)
                {
                    logger.LogInformation(
                        "The approved analysis policy manifest is published ({Reason}); publication stops here.",
                        result.Reason);
                    return;
                }

                if (result.Outcome == AnalysisPolicyPublishOutcome.CONFLICT)
                {
                    // An immutable-identity conflict cannot be resolved by retrying, and retrying
                    // would only repeat the same refusal. Claims stay closed until the deployment
                    // declares a manifest that matches the persisted identity.
                    logger.LogError(
                        "The declared analysis policy manifest conflicts with the persisted identity ({Reason}); analysis claims stay closed and publication stops.",
                        result.Reason);
                    return;
                }

                // A manifest that is not a publishable approved identity is a configuration error:
                // retrying it can never succeed, so publication stops instead of churning.
                logger.LogError(
                    "The declared analysis policy manifest was rejected ({Reason}); analysis claims stay closed and publication stops.",
                    result.Reason);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Only a transient failure is retried. A permanent provider, permission, mapping or
                // configuration fault would never succeed on a later attempt, so retrying it would
                // only disguise a real deployment fault as a temporary outage until the attempts run
                // out. It is surfaced immediately instead, with the readiness gate keeping claims
                // closed for as long as the approved row is missing.
                if (!IsTransient(ex))
                {
                    logger.LogError(
                        ex,
                        "Publishing the approved analysis policy manifest failed permanently on attempt {Attempt}; analysis claims stay closed until the fault is fixed.",
                        attempt);
                    return;
                }

                logger.LogWarning(
                    ex,
                    "Publishing the approved analysis policy manifest hit a transient failure on attempt {Attempt}; analysis claims stay closed until it succeeds.",
                    attempt);
                settled = true;
            }

            if (!settled || attempt == MAX_ATTEMPTS)
            {
                break;
            }

            try
            {
                await Task.Delay(retryDelay, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, _maxRetryDelay.Ticks));
        }

        logger.LogError(
            "The approved analysis policy manifest was not published after {Attempts} transient attempts; analysis claims stay closed until a later successful publication.",
            MAX_ATTEMPTS);
    }

    /// <summary>
    /// Narrow transient classification, consistent with the existing Npgsql resilience behaviour: a
    /// provider fault Npgsql itself marks transient, a socket or stream fault, or a timeout. Anything
    /// else, including configuration, mapping, permission and programming faults, is permanent.
    /// </summary>
    private static bool IsTransient(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or TimeoutException or IOException)
            {
                return true;
            }
        }

        return false;
    }

    private static AnalysisPolicyManifest ToManifest(AnalysisPolicyManifestOptions declared) =>
        new(declared.PolicyKey,
            declared.PolicyDigest,
            declared.SchemaName,
            declared.StageMatrixJson,
            declared.VerifierMode,
            declared.VerifierReason,
            declared.ToolIdentitiesJson,
            declared.LimitsJson,
            declared.SecurityValidityJson,
            declared.ModelKey,
            declared.IndexKey,
            declared.ReferenceSnapshotId);
}
