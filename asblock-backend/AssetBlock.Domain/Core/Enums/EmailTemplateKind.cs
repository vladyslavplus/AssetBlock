using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Transactional email template discriminators (stable outbox wire strings).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailTemplateKind
{
    PURCHASE_RECEIPT,
    ASSET_SOLD,
    EMAIL_VERIFICATION,
    PASSWORD_RESET,
    EMAIL_CHANGE_CONFIRMATION,
    PASSWORD_CHANGED_NOTICE,
    EMAIL_CHANGED_NOTICE
}
