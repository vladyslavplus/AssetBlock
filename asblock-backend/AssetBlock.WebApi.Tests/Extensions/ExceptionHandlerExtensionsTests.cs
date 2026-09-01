using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class ExceptionHandlerExtensionsTests
{
    [Fact]
    public async Task Run_WhenUnhandledException_ShouldLogErrorWithTraceIdAndReturn500()
    {
        const string traceId = "test-trace-id";
        var logger = new RecordingLogger<ExceptionHandlerLog>();
        await using WebApplication app = CreateApp(logger, traceId, new InvalidOperationException("boom"));

        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/boom", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await AssertInternalProblemAsync(response, traceId);

        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error
            && e.Exception is InvalidOperationException
            && e.Message.Contains(traceId));
    }

    [Fact]
    public async Task Run_WhenValidationException_ShouldNotLogError()
    {
        var logger = new RecordingLogger<ExceptionHandlerLog>();
        var validationException = new ValidationException([
            new ValidationFailure("password", "Password is too short.")
        ]);
        await using WebApplication app = CreateApp(logger, "validation-trace", validationException);

        await app.StartAsync();
        HttpClient client = app.GetTestClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/boom", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Run_WhenCacheUnavailable_ShouldLogWarningAndReturn503()
    {
        const string traceId = "cache-trace";
        var logger = new RecordingLogger<ExceptionHandlerLog>();
        await using WebApplication app = CreateApp(
            logger,
            traceId,
            new CacheUnavailableException("redis down"));

        await app.StartAsync();
        HttpResponseMessage response = await app.GetTestClient().GetAsync(new Uri("/boom", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);
        doc.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_SERVICE_UNAVAILABLE);
        doc.RootElement.GetProperty("traceId").GetString().Should().Be(traceId);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.Exception is CacheUnavailableException
            && entry.Message.Contains(traceId));
        logger.Entries.Should().NotContain(entry => entry.Level == LogLevel.Error);
    }

    private static WebApplication CreateApp(
        RecordingLogger<ExceptionHandlerLog> logger,
        string traceId,
        Exception exceptionToThrow)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ILogger<ExceptionHandlerLog>>(logger);

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.TraceIdentifier = traceId;
            await next();
        });
        app.UseValidationExceptionHandler();
        app.MapGet("/boom", _ => throw exceptionToThrow);

        return app;
    }

    private static async Task AssertInternalProblemAsync(HttpResponseMessage response, string traceId)
    {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument doc = await JsonDocument.ParseAsync(stream);
        JsonElement root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status500InternalServerError);
        root.GetProperty("type").GetString().Should().Be($"urn:assetblock:error:{ErrorCodes.ERR_INTERNAL}");
        root.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_INTERNAL);
        root.GetProperty("traceId").GetString().Should().Be(traceId);
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
