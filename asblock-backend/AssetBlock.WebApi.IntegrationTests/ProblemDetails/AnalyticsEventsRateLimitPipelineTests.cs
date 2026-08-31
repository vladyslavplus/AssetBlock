using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace AssetBlock.WebApi.IntegrationTests.ProblemDetails;

public sealed class AnalyticsEventsRateLimitPipelineTests
{
    [Fact]
    public async Task DirectPartition_WhenSameRemoteIpExceedsLimit_ShouldReturn429AndOtherIpAccepted()
    {
        await using WebApplication app = await AnalyticsRateLimitTestHost.StartAsync();
        HttpClient client = app.GetTestClient();
        const string ipA = "203.0.113.1";
        const string ipB = "203.0.113.2";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            HttpResponseMessage ok = await AnalyticsRateLimitTestHost.PostProbeAsync(client, ipA);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, $"direct request {i + 1} for IP A");
        }

        HttpResponseMessage limited = await AnalyticsRateLimitTestHost.PostProbeAsync(client, ipA);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        await AssertRateLimitedProblemDetails(limited);

        HttpResponseMessage otherIp = await AnalyticsRateLimitTestHost.PostProbeAsync(client, ipB);
        otherIp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task BffPartition_WhenSameRemoteIpDifferentPartitions_ShouldRateLimitIndependently()
    {
        await using WebApplication app = await AnalyticsRateLimitTestHost.StartAsync();
        HttpClient client = app.GetTestClient();
        const string remoteIp = "203.0.113.50";
        const string clientIpA = "198.51.100.1";
        const string clientIpB = "198.51.100.2";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            Dictionary<string, string?> headersA = AnalyticsRateLimitTestHost.CreateSignedHeaders(
                clientIpA,
                AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET);
            HttpResponseMessage ok = await AnalyticsRateLimitTestHost.PostProbeAsync(client, remoteIp, headersA);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, $"bff partition A request {i + 1}");
        }

        Dictionary<string, string?> limitedHeadersA = AnalyticsRateLimitTestHost.CreateSignedHeaders(
            clientIpA,
            AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET);
        HttpResponseMessage limited = await AnalyticsRateLimitTestHost.PostProbeAsync(client, remoteIp, limitedHeadersA);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        await AssertRateLimitedProblemDetails(limited);

        HttpResponseMessage otherPartition = await AnalyticsRateLimitTestHost.PostProbeAsync(
            client,
            remoteIp,
            AnalyticsRateLimitTestHost.CreateSignedHeaders(
                clientIpB,
                AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET));
        otherPartition.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task InvalidBffSignature_ShouldReturn403AndNotConsumeDirectPartition()
    {
        await using WebApplication app = await AnalyticsRateLimitTestHost.StartAsync();
        HttpClient client = app.GetTestClient();
        const string directIp = "203.0.113.99";
        const string clientIp = "198.51.100.77";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            HttpResponseMessage ok = await AnalyticsRateLimitTestHost.PostProbeAsync(client, directIp);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        HttpResponseMessage saturatedDirect = await AnalyticsRateLimitTestHost.PostProbeAsync(client, directIp);
        saturatedDirect.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var invalidCases = new (string Description, IReadOnlyDictionary<string, string?> Headers)[]
        {
            ("partial partition", new Dictionary<string, string?>
            {
                [AnalyticsBffRateLimitHeaders.PARTITION] = new string('a', 64),
            }),
            ("bad signature", CreateHeadersWithBadSignature(clientIp)),
            ("stale timestamp", AnalyticsRateLimitTestHost.CreateSignedHeaders(
                clientIp,
                AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET,
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds())),
            ("malformed partition", new Dictionary<string, string?>
            {
                [AnalyticsBffRateLimitHeaders.PARTITION] = "not-valid-hex",
                [AnalyticsBffRateLimitHeaders.TIMESTAMP] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                [AnalyticsBffRateLimitHeaders.SIGNATURE] = new string('b', 64),
            }),
            ("wrong secret", AnalyticsRateLimitTestHost.CreateSignedHeaders(
                clientIp,
                "wrong_secret_at_least_32_characters_long!!")),
        };

        foreach ((var description, IReadOnlyDictionary<string, string?>? headers) in invalidCases)
        {
            HttpResponseMessage invalid = await AnalyticsRateLimitTestHost.PostProbeAsync(client, directIp, headers);
            invalid.StatusCode.Should().Be(HttpStatusCode.Forbidden, description);
            await AssertInvalidSignatureProblemDetails(invalid);
        }

        HttpResponseMessage validOtherPartition = await AnalyticsRateLimitTestHost.PostProbeAsync(
            client,
            directIp,
            AnalyticsRateLimitTestHost.CreateSignedHeaders("198.51.100.88", AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET));
        validOtherPartition.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task DirectWithoutHeaders_WorksAndForgedPartitionWithoutSignature_ShouldReturn403()
    {
        await using WebApplication app = await AnalyticsRateLimitTestHost.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage direct = await AnalyticsRateLimitTestHost.PostProbeAsync(client, "203.0.113.10");
        direct.StatusCode.Should().Be(HttpStatusCode.Accepted);

        HttpResponseMessage forged = await AnalyticsRateLimitTestHost.PostProbeAsync(
            client,
            "203.0.113.10",
            new Dictionary<string, string?>
            {
                [AnalyticsBffRateLimitHeaders.PARTITION] = new string('c', 64),
            });
        forged.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await AssertInvalidSignatureProblemDetails(forged);
    }

    private static Dictionary<string, string?> CreateHeadersWithBadSignature(string clientIp)
    {
        Dictionary<string, string?> headers = AnalyticsRateLimitTestHost.CreateSignedHeaders(
            clientIp,
            AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET);
        headers[AnalyticsBffRateLimitHeaders.SIGNATURE] = new string('d', 64);
        return headers;
    }

    private static async Task AssertRateLimitedProblemDetails(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_RATE_LIMITED);
    }

    private static async Task AssertInvalidSignatureProblemDetails(HttpResponseMessage response)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_ANALYTICS_BFF_SIGNATURE_INVALID);
    }
}
