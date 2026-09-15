using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class RecommendationExposureSignerTests
{
    private const string SECRET = "unit_test_analytics_bff_signing_secret_32";

    [Fact]
    public void TryVerify_WhenPayloadMatchesToken_ShouldReturnTrue()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        RecommendationExposurePayload payload = Payload();

        var token = signer.TryCreateToken(payload);

        token.Should().HaveLength(RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH);
        signer.TryVerify(payload, token).Should().BeTrue();
    }

    [Fact]
    public void TryVerify_WhenCandidateOrderChanges_ShouldReturnFalse()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        RecommendationExposurePayload payload = Payload(candidates: [first, second]);
        var token = signer.TryCreateToken(payload)!;

        signer.TryVerify(payload with { CandidateIds = [second, first] }, token).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WhenTokenIsForged_ShouldReturnFalse()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        RecommendationExposurePayload payload = Payload();
        var token = signer.TryCreateToken(payload)!;
        var forged = (token[0] == 'a' ? 'b' : 'a') + token[1..];

        signer.TryVerify(payload, forged).Should().BeFalse();
    }

    [Fact]
    public void TryCreateToken_WhenSecretMissing_ShouldReturnNull()
    {
        RecommendationExposureSigner signer = CreateSigner("   ");
        signer.TryCreateToken(Payload()).Should().BeNull();
        signer.TryVerify(Payload(), new string('a', 64)).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WhenAudienceMatchesActor_ShouldReturnTrue()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        var audience = Guid.NewGuid();
        RecommendationExposurePayload payload = Payload() with { AudienceUserId = audience };
        var token = signer.TryCreateToken(payload)!;

        token.Should().HaveLength(RecommendationTelemetryConstants.EXPOSURE_TOKEN_LENGTH);
        signer.TryVerify(payload, token).Should().BeTrue();
    }

    [Fact]
    public void TryVerify_WhenAudienceDiffersFromActor_ShouldReturnFalse()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        RecommendationExposurePayload payload = Payload() with { AudienceUserId = Guid.NewGuid() };
        var token = signer.TryCreateToken(payload)!;

        signer.TryVerify(payload with { AudienceUserId = Guid.NewGuid() }, token).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WhenAudienceBoundTokenVerifiedWithoutAudience_ShouldReturnFalse()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        RecommendationExposurePayload payload = Payload() with { AudienceUserId = Guid.NewGuid() };
        var token = signer.TryCreateToken(payload)!;

        signer.TryVerify(payload with { AudienceUserId = null }, token).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_WhenAudienceAbsent_ShouldStayBackwardCompatible()
    {
        RecommendationExposureSigner signer = CreateSigner(SECRET);
        RecommendationExposurePayload payload = Payload();
        var token = signer.TryCreateToken(payload)!;

        signer.TryVerify(payload, token).Should().BeTrue();
    }

    private static RecommendationExposureSigner CreateSigner(string secret) =>
        new(Microsoft.Extensions.Options.Options.Create(new AnalyticsRateLimitingOptions { BffSigningSecret = secret }));

    private static RecommendationExposurePayload Payload(IReadOnlyList<Guid>? candidates = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            new DateTimeOffset(2026, 9, 13, 12, 15, 0, TimeSpan.Zero),
            candidates ?? [Guid.NewGuid()]);
}
