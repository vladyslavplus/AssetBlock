using AssetBlock.Domain.Core.Dto.Email;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Application-owned composer contract used by Infrastructure action delivery.</summary>
public interface ITransactionalEmailComposer
{
    EmailDispatchPayload CreatePurchaseReceipt(
        string recipientAddress,
        Guid recipientUserId,
        string assetTitle,
        DateTimeOffset purchasedAt);

    EmailDispatchPayload CreateAssetSold(
        string recipientAddress,
        Guid recipientUserId,
        string assetTitle,
        DateTimeOffset purchasedAt);

    EmailDispatchPayload CreatePasswordChangedNotice(string recipientAddress, Guid recipientUserId);

    EmailDispatchPayload CreateEmailChangedNotice(string recipientAddress, Guid recipientUserId);

    EmailMessage CreateEmailVerification(
        string recipientAddress,
        Guid recipientUserId,
        string actionUrl);

    EmailMessage CreatePasswordReset(
        string recipientAddress,
        Guid recipientUserId,
        string actionUrl);

    EmailMessage CreateEmailChangeConfirmation(
        string recipientAddress,
        Guid recipientUserId,
        string actionUrl);
}
