using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Outbox;

internal static class ModerationEventIdentity
{
    public static Guid Event(Guid submissionId, long revision, NotificationKind kind) =>
        Hash(FormattableString.Invariant($"{submissionId:N}:{revision}:{kind}"));
    public static Guid Recipient(Guid eventId, Guid recipientId) => Hash($"{eventId:N}:{recipientId:N}");
    private static Guid Hash(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
