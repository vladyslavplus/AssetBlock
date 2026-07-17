using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Who initiated an audit event.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditActorType
{
    USER,
    SYSTEM,
    ANONYMOUS
}
