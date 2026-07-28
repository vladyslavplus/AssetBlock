using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class SellerAnalyticsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetOverview_Anonymous_Returns401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/seller/analytics/overview", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetProducts_Anonymous_Returns401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/seller/analytics/products", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSales_Anonymous_Returns401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/seller/analytics/sales", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOverview_UnverifiedUser_Returns403WithEmailNotVerified()
    {
        var (client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri("/api/seller/analytics/overview", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }


    [Fact]
    public async Task GetOverview_VerifiedUserNoSales_Returns200WithZeroKpis()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/overview?from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("grossRevenue").GetProperty("current").GetInt64().Should().Be(0);
        root.GetProperty("orders").GetProperty("current").GetInt64().Should().Be(0);
        root.GetProperty("unitsSold").GetProperty("current").GetInt64().Should().Be(0);
        root.GetProperty("currency").GetString().Should().Be("usd");
        root.GetProperty("series").GetArrayLength().Should().BeGreaterThan(0); // zero-filled
        root.GetProperty("topAssets").GetArrayLength().Should().Be(0);
        root.GetProperty("topBundles").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetProducts_VerifiedUserNoSales_Returns200WithEmptyItems()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/products?from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetSales_VerifiedUserNoSales_Returns200WithEmptyItems()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/sales?from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }


    [Fact]
    public async Task GetOverview_ToBeforeFrom_Returns400()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/overview?from=2024-06-01&to=2024-01-01", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetOverview_ToAfterTomorrowUtc_Returns400()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var futureDate = DateTime.UtcNow.AddDays(10).ToString("yyyy-MM-dd");
        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/overview?from=2024-01-01&to={futureDate}", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetProducts_InvalidPageSize_Returns400()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/products?from=2024-01-01&to=2024-02-01&pageSize=999", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetSales_InvalidCursor_Returns400()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(
            new Uri("/api/seller/analytics/sales?from=2024-01-01&to=2024-02-01&cursor=not_valid!!!", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }


    [Fact]
    public async Task GetOverview_SellerOnlySeeOwnSales()
    {
        // Seller A and Seller B both verified
        var (clientA, usernameA) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var (clientB, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        // Seed a purchase for Seller A's asset
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var sellerA = await db.Users.SingleAsync(u => u.Username == usernameA);
            var category = await db.Categories.FirstAsync();

            var asset = new Domain.Core.Entities.Asset
            {
                Id = Guid.NewGuid(),
                AuthorId = sellerA.Id,
                CategoryId = category.Id,
                Title = "Isolation Test Asset",
                Price = 20m,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Assets.Add(asset);
            await db.SaveChangesAsync();

            var version = new Domain.Core.Entities.AssetVersion
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = $"assets/{asset.Id:N}/v1.bin",
                FileName = "file.zip",
                ContentLength = 1,
                ContentSha256 = new string('0', 64),
                ReleaseNotes = "v1",
                LicenseCode = Domain.Core.Enums.AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "terms",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.AssetVersions.Add(version);
            await db.SaveChangesAsync();

            // Seed the order
            var intentId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            var session = $"test-stripe-iso-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow.AddDays(-1);

            db.CheckoutIntents.Add(new Domain.Core.Entities.CheckoutIntent
            {
                Id = intentId,
                UserId = sellerA.Id,
                AssetId = asset.Id,
                ProductTitle = asset.Title,
                AmountTotal = 20m,
                Currency = "usd",
                StripeSessionId = session,
                Status = Domain.Core.Enums.CheckoutIntentStatus.COMPLETED,
                CreatedAt = now,
                ExpiresAt = now.AddHours(1),
                CompletedAt = now
            });
            db.CheckoutIntentItems.Add(new Domain.Core.Entities.CheckoutIntentItem
            {
                Id = Guid.NewGuid(),
                CheckoutIntentId = intentId,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                SellerId = sellerA.Id,
                Position = 1,
                AssetTitleSnapshot = asset.Title,
                VersionNumber = 1,
                ListPrice = 20m,
                AllocatedPrice = 20m,
                LicenseCode = Domain.Core.Enums.AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "terms"
            });
            db.Orders.Add(new Domain.Core.Entities.Order
            {
                Id = orderId,
                UserId = sellerA.Id,
                CheckoutIntentId = intentId,
                AssetId = asset.Id,
                ProductTitle = asset.Title,
                StripeSessionId = session,
                AmountPaid = 20m,
                Currency = "usd",
                PurchasedAt = now,
                CreatedAt = now
            });
            db.OrderLines.Add(new Domain.Core.Entities.OrderLine
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                SellerId = sellerA.Id,
                Position = 1,
                AssetTitleSnapshot = asset.Title,
                VersionNumber = 1,
                ListPrice = 20m,
                PricePaid = 20m,
                LicenseCode = Domain.Core.Enums.AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "terms"
            });
            await db.SaveChangesAsync();
        }

        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        // Seller A should see the sale
        var respA = await clientA.GetAsync(
            new Uri($"/api/seller/analytics/overview?from={from}&to={to}", UriKind.Relative));
        respA.StatusCode.Should().Be(HttpStatusCode.OK);
        var docA = JsonDocument.Parse(await respA.Content.ReadAsStringAsync());
        docA.RootElement.GetProperty("grossRevenue").GetProperty("current").GetInt64().Should().Be(2000);

        // Seller B should see nothing
        var respB = await clientB.GetAsync(
            new Uri($"/api/seller/analytics/overview?from={from}&to={to}", UriKind.Relative));
        respB.StatusCode.Should().Be(HttpStatusCode.OK);
        var docB = JsonDocument.Parse(await respB.Content.ReadAsStringAsync());
        docB.RootElement.GetProperty("grossRevenue").GetProperty("current").GetInt64().Should().Be(0);
    }


    [Fact]
    public async Task GetOverview_VerifiedUser_ResponseShapeContainsAllRequiredFields()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var from = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
        var to = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        var response = await client.GetAsync(
            new Uri($"/api/seller/analytics/overview?from={from}&to={to}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.TryGetProperty("from", out _).Should().BeTrue();
        root.TryGetProperty("to", out _).Should().BeTrue();
        root.TryGetProperty("comparisonFrom", out _).Should().BeTrue();
        root.TryGetProperty("comparisonTo", out _).Should().BeTrue();
        root.TryGetProperty("granularity", out _).Should().BeTrue();
        root.TryGetProperty("currency", out _).Should().BeTrue();
        root.TryGetProperty("grossRevenue", out _).Should().BeTrue();
        root.TryGetProperty("directRevenue", out _).Should().BeTrue();
        root.TryGetProperty("bundleRevenue", out _).Should().BeTrue();
        root.TryGetProperty("orders", out _).Should().BeTrue();
        root.TryGetProperty("unitsSold", out _).Should().BeTrue();
        root.TryGetProperty("averageOrderValue", out _).Should().BeTrue();
        root.TryGetProperty("uniqueCustomers", out _).Should().BeTrue();
        root.TryGetProperty("series", out _).Should().BeTrue();
        root.TryGetProperty("topAssets", out _).Should().BeTrue();
        root.TryGetProperty("topBundles", out _).Should().BeTrue();
    }
}
