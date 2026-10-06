using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class JwtLiveRoleAuthenticationTests
{
    private const string JWT_KEY = "test_secret_key_with_at_least_32_characters_length!";
    private const string JWT_ISSUER = "AssetBlock.Test";
    private const string JWT_AUDIENCE = "AssetBlock.Test.Api";
    private const string JWT_HUB_AUDIENCE = "AssetBlock.Test.Hub";

    [Fact]
    public async Task Authenticate_WhenTokenHasStaleModeratorClaim_ShouldUsePersistedUserRole()
    {
        var userId = Guid.NewGuid();
        IAuthenticatedUserRoleResolver resolver = Substitute.For<IAuthenticatedUserRoleResolver>();
        resolver.Resolve(userId, Arg.Any<CancellationToken>())
            .Returns(new UserPersistedRole(userId, AppRoles.USER, 3));

        await using WebApplication app = CreateApp(resolver);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        var token = CreateApiToken(userId, AppRoles.MODERATOR);

        HttpResponseMessage response = await client.SendAsync(AuthorizedGet("/api/moderator-only", token));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Authenticate_WhenPersistedModeratorRole_ShouldAllowModeratorEndpoint()
    {
        var userId = Guid.NewGuid();
        IAuthenticatedUserRoleResolver resolver = Substitute.For<IAuthenticatedUserRoleResolver>();
        resolver.Resolve(userId, Arg.Any<CancellationToken>())
            .Returns(new UserPersistedRole(userId, AppRoles.MODERATOR, 2));

        await using WebApplication app = CreateApp(resolver);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        var token = CreateApiToken(userId, AppRoles.USER);

        HttpResponseMessage response = await client.SendAsync(AuthorizedGet("/api/moderator-only", token));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Authenticate_WhenUserMissing_ShouldReturn401()
    {
        var userId = Guid.NewGuid();
        IAuthenticatedUserRoleResolver resolver = Substitute.For<IAuthenticatedUserRoleResolver>();
        resolver.Resolve(userId, Arg.Any<CancellationToken>())
            .Returns((UserPersistedRole?)null);

        await using WebApplication app = CreateApp(resolver);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        var token = CreateApiToken(userId, AppRoles.USER);

        HttpResponseMessage response = await client.SendAsync(AuthorizedGet("/api/protected", token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticate_WhenMalformedSubject_ShouldReturn401()
    {
        IAuthenticatedUserRoleResolver resolver = Substitute.For<IAuthenticatedUserRoleResolver>();
        await using WebApplication app = CreateApp(resolver);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        var token = CreateApiTokenWithSubject("not-a-guid", AppRoles.USER);

        HttpResponseMessage response = await client.SendAsync(AuthorizedGet("/api/protected", token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await resolver.DidNotReceive().Resolve(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Authenticate_WhenHubTokenOnRestScheme_ShouldReturn401()
    {
        var userId = Guid.NewGuid();
        IAuthenticatedUserRoleResolver resolver = Substitute.For<IAuthenticatedUserRoleResolver>();
        await using WebApplication app = CreateApp(resolver);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        var token = CreateHubToken(userId, AppRoles.USER);

        HttpResponseMessage response = await client.SendAsync(AuthorizedGet("/api/protected", token));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await resolver.DidNotReceive().Resolve(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static HttpRequestMessage AuthorizedGet(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static WebApplication CreateApp(IAuthenticatedUserRoleResolver roleResolver)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = JWT_KEY,
            ["Jwt:Issuer"] = JWT_ISSUER,
            ["Jwt:Audience"] = JWT_AUDIENCE,
            ["Jwt:HubAudience"] = JWT_HUB_AUDIENCE
        });

        builder.Services.AddSingleton(Substitute.For<ILogger<JwtBearerEvents>>());
        builder.Services.AddSingleton(roleResolver);
        builder.Services.AddJwtAuthentication(builder.Configuration);
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/protected", () => Microsoft.AspNetCore.Http.Results.Ok()).RequireAuthorization();
        app.MapGet("/api/moderator-only", () => Microsoft.AspNetCore.Http.Results.Ok())
            .RequireAuthorization(new AuthorizeAttribute { Roles = AppRoles.MODERATOR });

        return app;
    }

    private static string CreateApiToken(Guid userId, string role) =>
        CreateSignedToken(
            userId.ToString(),
            JWT_AUDIENCE,
            [new Claim(JwtClaimTypes.ROLE, role)]);

    private static string CreateApiTokenWithSubject(string subject, string role) =>
        CreateSignedToken(
            subject,
            JWT_AUDIENCE,
            [new Claim(JwtClaimTypes.ROLE, role)]);

    private static string CreateHubToken(Guid userId, string role) =>
        CreateSignedToken(
            userId.ToString(),
            JWT_HUB_AUDIENCE,
            [
                new Claim(JwtClaimTypes.ROLE, role),
                new Claim(JwtClaimTypes.TOKEN_USE, JwtClaimValues.TOKEN_USE_SIGNALR)
            ]);

    private static string CreateSignedToken(string subject, string audience, Claim[] extraClaims)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JWT_KEY));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtClaimTypes.SUB, subject),
            new(ClaimTypes.NameIdentifier, subject)
        };
        claims.AddRange(extraClaims);

        var token = new JwtSecurityToken(
            issuer: JWT_ISSUER,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
