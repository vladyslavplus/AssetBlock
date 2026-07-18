namespace AssetBlock.Domain.Core.Dto.Email;

/// <summary>Minimal recipient projection for transactional email (no social links).</summary>
public sealed record EmailRecipient(Guid Id, string Email);
