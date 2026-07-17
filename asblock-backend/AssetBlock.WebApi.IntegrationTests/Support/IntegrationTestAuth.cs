using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Infrastructure.Persistence;
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
        var client = factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var username = $"ig_{suffix}";
        var email = $"ig-{suffix}@test.local";
        const string password = "Password1!";

        var registerResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest(username, email, password));

        registerResponse.EnsureSuccessStatusCode();
        var tokens = await registerResponse.Content.ReadFromJsonAsync<TokensResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, username);
    }

    /// <summary>
    /// Registers a user, promotes them to Admin in the database, then re-authenticates so the JWT carries the Admin role.
    /// </summary>
    public static async Task<(HttpClient Client, string Username)> RegisterAdminAndAuthenticateAsync(
        WebApplicationFactory<Program> factory)
    {
        var (_, username) = await RegisterAndAuthenticateAsync(factory);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Username == username);
            user.Role = AppRoles.ADMIN;
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var email = await FindEmailAsync(factory, username);
        var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, "Password1!"));
        loginResponse.EnsureSuccessStatusCode();
        var tokens = await loginResponse.Content.ReadFromJsonAsync<TokensResponseDto>(JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);
        return (client, username);
    }

    private static async Task<string> FindEmailAsync(WebApplicationFactory<Program> factory, string username)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
        return user.Email;
    }
}
