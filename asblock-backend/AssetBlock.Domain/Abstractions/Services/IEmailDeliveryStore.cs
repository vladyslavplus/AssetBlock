using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IEmailDeliveryStore
{
    /// <summary>
    /// Atomically claims delivery rights for an outbox message.
    /// Returns CLAIMED with a new claimToken if successfully acquired,
    /// ALREADY_DELIVERED if delivery was already confirmed,
    /// or CONCURRENT_CONFLICT if actively claimed by another worker.
    /// </summary>
    Task<(DeliveryClaimStatus Status, Guid? ClaimToken)> TryClaimDelivery(
        Guid outboxMessageId,
        string messageId,
        string recipientAddress,
        Guid recipientUserId,
        EmailTemplateKind templateKind,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms delivery upon successful SMTP send if the claim token matches.
    /// Returns true if updated, false if claim expired or was lost.
    /// </summary>
    Task<bool> ConfirmDelivery(
        Guid outboxMessageId,
        Guid claimToken,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases an in-flight delivery claim if SMTP or preparation failed, allowing immediate retry.
    /// </summary>
    Task<bool> ReleaseClaim(
        Guid outboxMessageId,
        Guid claimToken,
        CancellationToken cancellationToken = default);
}
