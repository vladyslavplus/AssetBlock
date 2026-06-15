using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

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
}
