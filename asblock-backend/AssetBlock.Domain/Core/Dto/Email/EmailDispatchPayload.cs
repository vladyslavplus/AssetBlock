using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Email;

/// <summary>Persisted outbox payload for <c>EmailDispatch</c> (no provider-specific fields).</summary>
public sealed record EmailDispatchPayload(
    string RecipientAddress,
    Guid RecipientUserId,
    EmailTemplateKind TemplateKind,
    string Subject,
    string TextBody,
    string HtmlBody);
