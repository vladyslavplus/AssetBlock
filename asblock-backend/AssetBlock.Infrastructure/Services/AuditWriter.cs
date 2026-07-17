using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AuditWriter(
    IAuditStore auditStore,
    IAuditContextAccessor auditContextAccessor,
    ILogger<AuditWriter> logger) : IAuditWriter
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false
    };

    public async Task Write(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        var entry = BuildEntry(auditEvent);
        await auditStore.Add(entry, cancellationToken);
    }

    public async Task WriteBestEffort(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            await Write(auditEvent, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Best-effort audit write failed for action {Action} outcome {Outcome}",
                auditEvent.Action,
                auditEvent.Outcome);
        }
    }

    private AuditLog BuildEntry(AuditEvent auditEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(auditEvent.ResourceType);

        var context = auditContextAccessor.GetCurrent();
        var (actorType, actorUserId) = ResolveActor(auditEvent, context);

        return new AuditLog
        {
            OccurredAt = DateTimeOffset.UtcNow,
            ActorType = actorType,
            ActorUserId = actorUserId,
            Action = NormalizeRequired(
                auditEvent.Action,
                AuditFieldLimits.ACTION_MAX_LENGTH,
                nameof(auditEvent.Action)),
            Outcome = auditEvent.Outcome,
            ResourceType = NormalizeRequired(
                auditEvent.ResourceType,
                AuditFieldLimits.RESOURCE_TYPE_MAX_LENGTH,
                nameof(auditEvent.ResourceType)),
            ResourceId = NormalizeOptional(
                auditEvent.ResourceId,
                AuditFieldLimits.RESOURCE_ID_MAX_LENGTH,
                nameof(auditEvent.ResourceId)),
            TraceId = Truncate(Sanitize(context?.TraceId), AuditFieldLimits.TRACE_ID_MAX_LENGTH),
            IpAddress = Truncate(Sanitize(context?.IpAddress), AuditFieldLimits.IP_ADDRESS_MAX_LENGTH),
            UserAgent = Truncate(Sanitize(context?.UserAgent), AuditFieldLimits.USER_AGENT_MAX_LENGTH),
            MetadataJson = SerializeMetadata(auditEvent.Metadata)
        };
    }

    private static (AuditActorType ActorType, Guid? ActorUserId) ResolveActor(
        AuditEvent auditEvent,
        CurrentAuditContext? context)
    {
        if (auditEvent.ActorTypeOverride is { } explicitType)
        {
            return (explicitType, auditEvent.ActorUserIdOverride);
        }

        if (context is null)
        {
            return (AuditActorType.SYSTEM, null);
        }

        return (context.ActorType, context.ActorUserId);
    }

    private static string? SerializeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return null;
        }

        var json = JsonSerializer.Serialize(metadata, _jsonOptions);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        if (byteCount > AuditFieldLimits.MAX_METADATA_BYTES)
        {
            throw new InvalidOperationException(
                $"Audit metadata exceeds {AuditFieldLimits.MAX_METADATA_BYTES} bytes (was {byteCount}).");
        }

        return json;
    }

    private static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        var cleaned = builder.ToString();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (value is null)
        {
            return null;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string NormalizeRequired(string value, int maxLength, string parameterName)
    {
        var normalized = Sanitize(value);
        if (normalized is null)
        {
            throw new ArgumentException("Audit field cannot be empty.", parameterName);
        }

        return EnsureLength(normalized, maxLength, parameterName);
    }

    private static string? NormalizeOptional(string? value, int maxLength, string parameterName)
    {
        var normalized = Sanitize(value);
        return normalized is null ? null : EnsureLength(normalized, maxLength, parameterName);
    }

    private static string EnsureLength(string value, int maxLength, string parameterName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Audit field exceeds maximum length of {maxLength} characters.",
                parameterName);
        }

        return value;
    }
}
