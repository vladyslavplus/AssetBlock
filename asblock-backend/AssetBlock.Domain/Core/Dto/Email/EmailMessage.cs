using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Email;

/// <summary>Provider-neutral message ready for transport delivery.</summary>
public sealed record EmailMessage(
    string RecipientAddress,
    Guid RecipientUserId,
    string Subject,
    string TextBody,
    string HtmlBody,
    EmailTemplateKind TemplateKind,
    string MessageId);
