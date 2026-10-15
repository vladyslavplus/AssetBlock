using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Infrastructure.Services;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class CodeAnalysisClientClassifyTests
{
    private static CodeAnalysisMetadataRequest Metadata() => new(
        "11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222",
        "33333333-3333-3333-3333-333333333333",
        new string('a', 64),
        new string('b', 64),
        "code-analysis-policy-v1",
        new string('c', 64),
        new string('d', 64),
        new CodeAnalysisReservation("code-analysis", 0, 1, new string('e', 64), "2030-01-01T00:00:00.0000000Z"),
        new CodeAnalysisSecurityEvidence(
            new string('a', 64),
            "44444444-4444-4444-4444-444444444444",
            "55555555-5555-5555-5555-555555555555",
            1),
        []);

    private static string Envelope(object body) => JsonSerializer.Serialize(body);

    [Fact]
    public void Classify_WhenCompletedWithNullCleanup_ShouldConfirmTerminated()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "COMPLETED",
            runId = metadata.RunId,
            inputDigest = metadata.InputDigest,
            contentSha256 = metadata.ContentSha256,
            cleanupError = (string?)null
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.CONFIRMED_TERMINATED);
        response.ResponseBody.Should().Be(body);
    }

    [Fact]
    public void Classify_WhenCompletedWithCleanupFailedValue_ShouldStayUnconfirmed()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "COMPLETED",
            runId = metadata.RunId,
            inputDigest = metadata.InputDigest,
            contentSha256 = metadata.ContentSha256,
            cleanupError = "CLEANUP_FAILED"
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.UNCONFIRMED);
        response.ResponseBody.Should().BeNull();
    }

    [Fact]
    public void Classify_WhenCancelledWithDeadlineExceeded_ShouldConfirmTerminated()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "CANCELLED",
            runId = metadata.RunId,
            inputDigest = metadata.InputDigest,
            contentSha256 = metadata.ContentSha256,
            errorCode = "DEADLINE_EXCEEDED",
            cleanupError = (string?)null
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.CONFIRMED_TERMINATED);
    }

    [Fact]
    public void Classify_WhenFailedWithStageError_ShouldConfirmTerminated()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "FAILED",
            runId = metadata.RunId,
            inputDigest = metadata.InputDigest,
            contentSha256 = metadata.ContentSha256,
            errorCode = "TOOL_TIMEOUT",
            cleanupError = (string?)null
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.CONFIRMED_TERMINATED);
    }

    [Fact]
    public void Classify_WhenServiceBusyRefusal_ShouldBeNotStarted()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            errorCode = "SERVICE_BUSY"
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.ServiceUnavailable, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.NOT_STARTED);
    }

    [Fact]
    public void Classify_WhenMixedOutcomeAndServiceBusy_ShouldStayUnconfirmed()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "FAILED",
            runId = metadata.RunId,
            inputDigest = metadata.InputDigest,
            contentSha256 = metadata.ContentSha256,
            errorCode = "SERVICE_BUSY",
            cleanupError = (string?)null
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.UNCONFIRMED);
    }

    [Fact]
    public void Classify_WhenWrongBindings_ShouldStayUnconfirmed()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            outcome = "COMPLETED",
            runId = metadata.RunId,
            inputDigest = new string('f', 64),
            contentSha256 = metadata.ContentSha256,
            cleanupError = (string?)null
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.UNCONFIRMED);
    }

    [Fact]
    public void Classify_WhenWedgedInternalError_ShouldStayUnconfirmed()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();
        var body = Envelope(new
        {
            schema = "code-analysis-v1",
            errorCode = "INTERNAL_ERROR"
        });

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.InternalServerError, body, metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.UNCONFIRMED);
    }

    [Fact]
    public void Classify_WhenHtmlBody_ShouldStayUnconfirmed()
    {
        CodeAnalysisMetadataRequest metadata = Metadata();

        CodeAnalysisServiceResponse response = CodeAnalysisClient.Classify(HttpStatusCode.OK, "<html>proxy</html>", metadata);

        response.Disposition.Should().Be(AnalysisExecutionDisposition.UNCONFIRMED);
    }
}
