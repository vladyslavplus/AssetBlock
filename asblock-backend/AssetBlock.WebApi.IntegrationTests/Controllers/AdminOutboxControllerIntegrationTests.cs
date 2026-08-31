using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class AdminOutboxControllerIntegrationTests(IntegrationTestFixture fixture)
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public async Task GetDeadLetters_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/admin/outbox/dead-letters?page=1&pageSize=20", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDeadLetters_WhenNonAdmin_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/admin/outbox/dead-letters?page=1&pageSize=20", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDeadLetters_WhenUnverifiedAdmin_ShouldReturn403EmailNotVerified()
    {
        (HttpClient _, var username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            User user = await db.Users.SingleAsync(u => u.Username == username);
            user.Role = AppRoles.ADMIN;
            await db.SaveChangesAsync();
        }

        HttpClient client = fixture.Factory.CreateClient();
        var email = await FindEmailAsync(username);
        HttpResponseMessage login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { Email = email, Password = "Password1!" });
        login.EnsureSuccessStatusCode();
        IntegrationTestAuth.TokensResponseDto? tokens = await login.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(
            IntegrationTestAuth.JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        HttpResponseMessage response = await client.GetAsync(new Uri("/api/admin/outbox/dead-letters?page=1&pageSize=20", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }

    [Fact]
    public async Task GetDeadLetters_WhenVerifiedAdmin_ShouldReturn200()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/admin/outbox/dead-letters?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        PagedResult<DeadLetterOutboxListItemDto>? page = await response.Content.ReadFromJsonAsync<PagedResult<DeadLetterOutboxListItemDto>>(_jsonOptions);
        page.Should().NotBeNull();
        page.Page.Should().Be(1);
        page.PageSize.Should().Be(20);
        page.TotalCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Replay_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/admin/outbox/dead-letters/{Guid.NewGuid()}/replay", UriKind.Relative),
            null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Replay_WhenNonAdmin_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/admin/outbox/dead-letters/{Guid.NewGuid()}/replay", UriKind.Relative),
            null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Replay_WhenUnverifiedAdmin_ShouldReturn403EmailNotVerified()
    {
        (HttpClient _, var username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            User user = await db.Users.SingleAsync(u => u.Username == username);
            user.Role = AppRoles.ADMIN;
            await db.SaveChangesAsync();
        }

        HttpClient client = fixture.Factory.CreateClient();
        var email = await FindEmailAsync(username);
        HttpResponseMessage login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new { Email = email, Password = "Password1!" });
        login.EnsureSuccessStatusCode();
        IntegrationTestAuth.TokensResponseDto? tokens = await login.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(
            IntegrationTestAuth.JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/admin/outbox/dead-letters/{Guid.NewGuid()}/replay", UriKind.Relative),
            null);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }

    [Fact]
    public async Task Replay_WhenVerifiedAdmin_AndNotFound_ShouldReturn404ProblemDetails()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/admin/outbox/dead-letters/{Guid.NewGuid()}/replay", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_OUTBOX_MESSAGE_NOT_FOUND);
    }

    private async Task<string> FindEmailAsync(string username)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
        return user.Email;
    }
}
