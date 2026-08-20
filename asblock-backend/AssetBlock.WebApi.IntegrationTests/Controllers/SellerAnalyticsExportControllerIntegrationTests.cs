using System.Net;
using System.Security.Claims;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure;
using AssetBlock.WebApi.Extensions;
using Microsoft.Extensions.Hosting;
using AssetBlock.WebApi.IntegrationTests.Support;
using AssetBlock.WebApi.IntegrationTests.Support.Fakes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class SellerAnalyticsExportControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ExportSales_Anonymous_Returns401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales/export?from=2024-01-01&to=2024-01-11", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ExportSales_UnverifiedUser_Returns403WithEmailNotVerified()
    {
        var (client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales/export?from=2024-01-01&to=2024-01-11", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }

    [Fact]
    public async Task ExportSales_VerifiedUserNoSales_ReturnsCsvWithHeaderOnly()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/sales/export?from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        response.Content.Headers.ContentType!.CharSet.Should().Be("utf-8");
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("sales-export_");
        response.Content.Headers.ContentDisposition!.FileName.Should().EndWith(".csv");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().Contain("purchased_at_utc,order_id,product_type,product_id,product_title,units,gross_revenue_usd");
        text.TrimEnd('\r', '\n').Split("\r\n").Should().HaveCount(1);
        text.ToLowerInvariant().Should().NotContain("stripe");
        text.ToLowerInvariant().Should().NotContain("buyer");
        text.ToLowerInvariant().Should().NotContain("email");
    }

    [Fact]
    public async Task ExportSales_InvalidRange_ReturnsProblemDetails()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales/export?from=2024-06-01&to=2024-01-01", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_ANALYTICS_INVALID_RANGE);
    }

    [Fact]
    public async Task ExportSales_WhenCapExceeded_ReturnsExportTooLarge()
    {
        var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISellerAnalyticsStore>();
                services.AddSingleton<ISellerAnalyticsStore, CapExceededSellerAnalyticsStore>();
            });
        });

        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales/export?from=2024-01-01&to=2024-02-01", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE);
    }

    [Fact]
    public async Task ExportSales_WhenSuccessful_WritesAuditOnce()
    {
        var (client, username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/sales/export?from={from}&to={to}", UriKind.Relative));
        response.EnsureSuccessStatusCode();

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var seller = await db.Users.SingleAsync(u => u.Username == username);
        var audits = await db.AuditLogs
            .Where(a => a.Action == AuditActions.SELLER_ANALYTICS_EXPORTED && a.ActorUserId == seller.Id)
            .ToListAsync();

        audits.Should().HaveCount(1);
        audits[0].ResourceType.Should().Be(AuditResourceTypes.SELLER_ANALYTICS);
        audits[0].MetadataJson.Should().Contain("rowCount");
        audits[0].MetadataJson.Should().NotContain("product_title");
    }

    [Fact]
    public async Task ExportSales_WhenCapExceeded_DoesNotWriteAudit()
    {
        var factory = fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISellerAnalyticsStore>();
                services.AddSingleton<ISellerAnalyticsStore, CapExceededSellerAnalyticsStore>();
            });
        });

        var (client, username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales/export?from=2024-01-01&to=2024-02-01", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var seller = await db.Users.SingleAsync(u => u.Username == username);
        var audits = await db.AuditLogs
            .Where(a => a.Action == AuditActions.SELLER_ANALYTICS_EXPORTED && a.ActorUserId == seller.Id)
            .ToListAsync();
        audits.Should().BeEmpty();
    }
}

public sealed class SellerAnalyticsExportRateLimitIntegrationTests
{
    [Fact]
    public async Task ExportSales_WhenRedisUnavailable_ShouldReturn503BeforeHandler()
    {
        await using var app = await SellerAnalyticsExportRateLimitTestHost.StartAsync(
            "127.0.0.1:1,abortConnect=false,connectTimeout=50");
        var client = app.GetTestClient();
        const string userId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

        var response = await SellerAnalyticsExportRateLimitTestHost.GetProbeAsync(client, userId);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_ANALYTICS_RATE_LIMIT_UNAVAILABLE);
    }

    [Fact]
    public async Task ExportSales_WhenSameUserExceedsLimit_ShouldReturn429()
    {
        await using var app = await SellerAnalyticsExportRateLimitTestHost.StartAsync();
        var client = app.GetTestClient();
        const string userId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

        for (var i = 0; i < RateLimitingConstants.Windows.SELLER_ANALYTICS_SALES_EXPORT_LIMIT; i++)
        {
            var ok = await SellerAnalyticsExportRateLimitTestHost.GetProbeAsync(client, userId);
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"export request {i + 1}");
        }

        var limited = await SellerAnalyticsExportRateLimitTestHost.GetProbeAsync(client, userId);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await limited.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_RATE_LIMITED);
    }
}

internal static class SellerAnalyticsExportRateLimitTestHost
{
    private const string TEST_USER_ID_HEADER = "X-Test-User-Id";
    private const string PROBE_PATH = "/probe/seller-analytics/sales/export";

    internal static async Task<WebApplication> StartAsync(string? redisConnectionString = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] =
            AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET;
        builder.Configuration["ConnectionStrings:Redis"] = redisConnectionString ?? string.Empty;
        var hostEnvironment = new TestHostEnvironment();
        builder.Services.AddSingleton<IHostEnvironment>(hostEnvironment);
        builder.Services.AddAnalyticsDistributedRateLimiting(builder.Configuration, hostEnvironment);
        builder.Services.AddApiRateLimiting();
        builder.Services.AddSingleton<ISellerAnalyticsStore, CapExceededSellerAnalyticsStore>();
        builder.Services.AddSingleton<ISender>(_ => throw new NotSupportedException());

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var userId = context.Request.Headers[TEST_USER_ID_HEADER].ToString();
            if (!string.IsNullOrWhiteSpace(userId))
            {
                var identity = new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId)],
                    authenticationType: "Test");
                context.User = new ClaimsPrincipal(identity);
            }

            await next();
        });
        app.UseRateLimiter();
        app.MapGet(PROBE_PATH, () => Microsoft.AspNetCore.Http.Results.Ok())
            .RequireRateLimiting(RateLimitingConstants.Policies.SELLER_ANALYTICS_SALES_EXPORT);

        await app.StartAsync();
        return app;
    }

    internal static async Task<HttpResponseMessage> GetProbeAsync(HttpClient client, string userId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(PROBE_PATH, UriKind.Relative));
        request.Headers.TryAddWithoutValidation(TEST_USER_ID_HEADER, userId);
        return await client.SendAsync(request);
    }
}
