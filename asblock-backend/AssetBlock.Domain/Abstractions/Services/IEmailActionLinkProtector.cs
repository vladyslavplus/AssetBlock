using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IEmailActionLinkProtector
{
    string Protect(EmailActionLinkClaims claims);

    bool TryUnprotect(string protectedToken, EmailActionPurpose expectedPurpose, out EmailActionLinkClaims claims);

    string BuildActionUrl(EmailActionPurpose purpose, string protectedToken);
}
