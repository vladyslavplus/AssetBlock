using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IEmailActionStore
{
    Task<EmailAction?> GetById(Guid id, CancellationToken cancellationToken = default);

    Task<EmailAction?> GetCurrent(Guid userId, EmailActionPurpose purpose, CancellationToken cancellationToken = default);

    /// <summary>Insert or update the single (UserId, Purpose) row with a fresh Version and LastSentAt.</summary>
    Task<EmailAction> IssueOrReplace(
        Guid userId,
        EmailActionPurpose purpose,
        string targetEmail,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Conditionally consume matching unexpired unconsumed action.
    /// Returns false without mutation when invalid/replaced/expired/consumed.
    /// </summary>
    Task<bool> TryConsume(
        Guid actionId,
        EmailActionPurpose purpose,
        Guid version,
        string expectedTargetEmail,
        CancellationToken cancellationToken = default);

    /// <summary>True when LastSentAt is within cooldown; does not mutate.</summary>
    bool IsInCooldown(EmailAction? action, TimeSpan cooldown, DateTimeOffset now);
}
