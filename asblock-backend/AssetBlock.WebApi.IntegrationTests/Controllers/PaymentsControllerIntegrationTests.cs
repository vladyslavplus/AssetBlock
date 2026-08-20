using System.Net;
using System.Net.Http.Json;
using AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PaymentsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task CreateCheckout_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateCheckout_WithAuth_WhenAssetMissing_ShouldReturnNotFound()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(Guid.Parse("d4e5f6a7-b8c9-0123-def0-456789abcdef")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_ASSET_NOT_FOUND");
        body.Should().Contain("traceId");
    }

    [Fact]
    public async Task Webhook_WithInvalidPayload_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/payments/webhook", UriKind.Relative),
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_STRIPE_WEBHOOK_INVALID");
    }

    [Fact]
    public async Task GetCheckoutStatus_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var intentId = Guid.NewGuid();

        var response = await client.GetAsync(
            new Uri($"/api/payments/checkout/{intentId}/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetCheckoutStatus_WhenForeignIntent_ShouldReturn404()
    {
        (HttpClient _, string user1Username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient user2Client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var user1Id = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, user1Username);
        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, user1Id, price: 9.99m);
        var intentId = Guid.NewGuid();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CheckoutIntents.Add(new CheckoutIntent
            {
                Id = intentId,
                UserId = user1Id,
                AssetId = assetId,
                ProductTitle = "Some Asset",
                AmountTotal = 9.99m,
                Currency = "usd",
                Status = CheckoutIntentStatus.PENDING,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        var response = await user2Client.GetAsync(
            new Uri($"/api/payments/checkout/{intentId}/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetCheckoutStatus_WhenPendingIntent_ShouldReturnPending()
    {
        (HttpClient client, string username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var userId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, username);
        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, userId, price: 5m);
        var intentId = Guid.NewGuid();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CheckoutIntents.Add(new CheckoutIntent
            {
                Id = intentId,
                UserId = userId,
                AssetId = assetId,
                ProductTitle = "Test Asset",
                AmountTotal = 5m,
                Currency = "usd",
                Status = CheckoutIntentStatus.PENDING,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            new Uri($"/api/payments/checkout/{intentId}/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetCheckoutStatusResponse>(IntegrationTestAuth.JsonOptions);
        body.Should().NotBeNull();
        body.Status.Should().Be("pending");
        body.CheckoutIntentId.Should().Be(intentId);
        body.OrderId.Should().BeNull();
    }

    [Fact]
    public async Task GetCheckoutStatus_WhenCancelledIntent_ShouldReturnCancelled()
    {
        (HttpClient client, string username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var userId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, username);
        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, userId, price: 8m);
        var intentId = Guid.NewGuid();

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CheckoutIntents.Add(new CheckoutIntent
            {
                Id = intentId,
                UserId = userId,
                AssetId = assetId,
                ProductTitle = "Cancelled Item",
                AmountTotal = 8m,
                Currency = "usd",
                Status = CheckoutIntentStatus.CANCELLED,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync(
            new Uri($"/api/payments/checkout/{intentId}/status", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetCheckoutStatusResponse>(IntegrationTestAuth.JsonOptions);
        body.Should().NotBeNull();
        body.Status.Should().Be("cancelled");
        body.CheckoutIntentId.Should().Be(intentId);
        body.OrderId.Should().BeNull();
    }
}
