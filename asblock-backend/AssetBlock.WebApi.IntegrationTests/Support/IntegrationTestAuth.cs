using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal static class IntegrationTestAuth
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed record TokensResponseDto(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset AccessExpiresAt,
        DateTimeOffset RefreshExpiresAt);

    public static async Task<(HttpClient Client, string Username)> RegisterAndAuthenticateAsync(
        WebApplicationFactory<Program> factory)
    {
        HttpClient client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var username = $"ig_{suffix}";
        var email = $"ig-{suffix}@test.local";
        const string password = "Password1!";

        HttpResponseMessage registerResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest(username, email, password));

        registerResponse.EnsureSuccessStatusCode();
        registerResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Accepted);

        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, password));
        loginResponse.EnsureSuccessStatusCode();
        TokensResponseDto? tokens = await loginResponse.Content.ReadFromJsonAsync<TokensResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, username);
    }

    /// <summary>
    /// Registers a user, promotes them to Admin in the database, marks email verified, then re-authenticates so the JWT carries the Admin role.
    /// </summary>
    public static async Task<(HttpClient Client, string Username)> RegisterAdminAndAuthenticateAsync(
        WebApplicationFactory<Program> factory)
    {
        (HttpClient _, var username) = await RegisterAndAuthenticateAsync(factory);

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            User user = await db.Users.SingleAsync(u => u.Username == username);
            user.Role = AppRoles.ADMIN;
            user.EmailVerifiedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        HttpClient client = factory.CreateClient();
        var email = await FindEmailAsync(factory, username);
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, "Password1!"));
        loginResponse.EnsureSuccessStatusCode();
        TokensResponseDto? tokens = await loginResponse.Content.ReadFromJsonAsync<TokensResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, username);
    }

    /// <summary>
    /// Registers and authenticates a user, then sets EmailVerifiedAt so VERIFIED_EMAIL policy succeeds.
    /// </summary>
    public static async Task<(HttpClient Client, string Username)> RegisterVerifiedAndAuthenticateAsync(
        WebApplicationFactory<Program> factory)
    {
        (HttpClient _, var username) = await RegisterAndAuthenticateAsync(factory);
        await MarkEmailVerifiedAsync(factory, username);
        HttpClient client = factory.CreateClient();
        var email = await FindEmailAsync(factory, username);
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, "Password1!"));
        loginResponse.EnsureSuccessStatusCode();
        TokensResponseDto? tokens = await loginResponse.Content.ReadFromJsonAsync<TokensResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, username);
    }

    public static async Task MarkEmailVerifiedAsync(WebApplicationFactory<Program> factory, string username)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User user = await db.Users.SingleAsync(u => u.Username == username);
        user.EmailVerifiedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static async Task<string> FindEmailAsync(WebApplicationFactory<Program> factory, string username)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
        return user.Email;
    }
}
