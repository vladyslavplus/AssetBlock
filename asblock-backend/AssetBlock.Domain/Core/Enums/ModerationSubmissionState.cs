using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Per-submission moderation lifecycle. WITHDRAWN uses a separate reason field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModerationSubmissionState
{
    DRAFT,
    SUBMITTED,
    IN_REVIEW,
    CHANGES_REQUESTED,
    APPROVED,
    REJECTED,
    WITHDRAWN
}
