using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Email;

/// <summary>Outbox payload for action emails. No token, URL, body, or target email.</summary>
public sealed record EmailActionDispatchPayload(
    Guid EmailActionId,
    Guid ActionVersion,
    Guid RecipientUserId,
    EmailTemplateKind TemplateKind);
