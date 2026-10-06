using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Services;
using AssetBlock.Infrastructure.Tests.Ai;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class AuditWriterTests
{
    private const string SENTINEL_PASSWORD = "SuperSecretPassword!";
    private const string SENTINEL_TOKEN = "eyJhbGciOiJIUzI1NiJ9.sentinel";
    private const string SENTINEL_EMAIL = "leak@example.com";

    [Fact]
    public async Task WriteBestEffort_WhenStoreFails_ShouldNotLogExceptionMessageOrAction()
    {
        IAuditStore auditStore = Substitute.For<IAuditStore>();
        auditStore
            .Add(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException(
                $"db failure password={SENTINEL_PASSWORD} token={SENTINEL_TOKEN} email={SENTINEL_EMAIL}"));

        var logger = new CollectingLogger<AuditWriter>();
        var writer = new AuditWriter(
            auditStore,
            Substitute.For<IAuditContextAccessor>(),
            logger);

        var auditEvent = new AuditEvent(
            $"User.Login password={SENTINEL_PASSWORD}",
            AuditOutcome.FAILURE,
            "User");

        await writer.WriteBestEffort(auditEvent, CancellationToken.None);

        var combined = string.Join('\n', logger.Messages);
        combined.Should().Contain("FAILURE");
        combined.Should().Contain(nameof(InvalidOperationException));
        combined.Should().NotContain(SENTINEL_PASSWORD);
        combined.Should().NotContain(SENTINEL_TOKEN);
        combined.Should().NotContain(SENTINEL_EMAIL);
        combined.Should().NotContain(auditEvent.Action);
    }
}
