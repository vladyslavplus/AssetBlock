using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Email;

/// <summary>Unprotected action-link claims verified against DB action state.</summary>
public sealed record EmailActionLinkClaims(
    Guid ActionId,
    Guid Version,
    EmailActionPurpose Purpose,
    DateTimeOffset ExpiresAt);
