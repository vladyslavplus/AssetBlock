using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class JwtAuthenticationExtensionsTests
{
    private const string JWT_KEY = "test_secret_key_with_at_least_32_characters_length!";
    private const string JWT_ISSUER = "AssetBlock.Test";
    private const string JWT_AUDIENCE = "AssetBlock.Test.Api";

    [Fact]
    public async Task Authenticate_WhenInvalidToken_ShouldLogReasonAtDebugAndReturn401()
    {
        var recordingLogger = new RecordingLogger<JwtBearerEvents>();
        await using WebApplication app = CreateApp(recordingLogger);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid.garbage.token");

        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);
        JsonElement root = doc.RootElement;
        root.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_AUTH_TOKEN_INVALID);

        // Assert exactly one log entry was created
        recordingLogger.Entries.Should().HaveCount(1);

        (LogLevel Level, Exception? Exception, string Message) entry = recordingLogger.Entries.Single();
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Exception.Should().BeNull();
        entry.Message.Should().NotContain("invalid.garbage.token");
        entry.Message.Should().MatchRegex("^JWT authentication failed: (malformed|invalid)$");
    }

    [Fact]
    public async Task Authenticate_WhenNoToken_ShouldLogSingleChallengeAtDebugAndReturn401()
    {
        var recordingLogger = new RecordingLogger<JwtBearerEvents>();
        await using WebApplication app = CreateApp(recordingLogger);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/protected");
        HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Assert exactly one log entry was created for missing token challenge
        recordingLogger.Entries.Should().HaveCount(1);

        (LogLevel Level, Exception? Exception, string Message) entry = recordingLogger.Entries.Single();
        entry.Level.Should().Be(LogLevel.Debug);
        entry.Exception.Should().BeNull();
        entry.Message.Should().Contain("Reason=missing_token");
    }

    private static WebApplication CreateApp(ILogger<JwtBearerEvents> logger)
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
            ["Jwt:Audience"] = JWT_AUDIENCE
        });

        builder.Services.AddSingleton(logger);
        builder.Services.AddJwtAuthentication(builder.Configuration);
        builder.Services.AddAuthorization();

        WebApplication app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/protected", () => Microsoft.AspNetCore.Http.Results.Ok()).RequireAuthorization();

        return app;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception, formatter(state, exception)));
        }
    }
}
