using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Outbox;

internal static class ModerationEventIdentity
{
    public static Guid Event(Guid submissionId, long revision, NotificationKind kind, bool isAppeal) =>
        Hash(FormattableString.Invariant($"{submissionId:N}:{revision}:{kind}:{isAppeal}"));
    public static Guid Decision(Guid decisionId, bool isAppeal) => Hash($"{decisionId:N}:{isAppeal}");
    public static Guid Recipient(Guid eventId, Guid recipientId) => Hash($"{eventId:N}:{recipientId:N}");
    private static Guid Hash(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 16));
}
