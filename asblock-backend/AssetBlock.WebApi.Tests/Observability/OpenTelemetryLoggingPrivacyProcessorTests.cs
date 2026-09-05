using AssetBlock.WebApi.Observability;
using AwesomeAssertions;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AssetBlock.WebApi.Tests.Observability;

public sealed class OpenTelemetryLoggingPrivacyProcessorTests
{
    private sealed class ExportedRecordSnapshot
    {
        public string? FormattedMessage { get; init; }
        public string? Body { get; init; }
        public List<KeyValuePair<string, object?>> Attributes { get; init; } = [];
    }

    private sealed class TestSnapshotExporter(List<ExportedRecordSnapshot> exportedRecords) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (LogRecord record in batch)
            {
                List<KeyValuePair<string, object?>> attrCopy = record.Attributes != null
                    ? new List<KeyValuePair<string, object?>>(record.Attributes)
                    : [];
                exportedRecords.Add(new ExportedRecordSnapshot
                {
                    FormattedMessage = record.FormattedMessage,
                    Body = record.Body,
                    Attributes = attrCopy
                });
            }

            return ExportResult.Success;
        }
    }

    private static (ILoggerFactory LoggerFactory, List<ExportedRecordSnapshot> Exported) CreateTestLoggingPipeline()
    {
        var exported = new List<ExportedRecordSnapshot>();
        ILoggerFactory factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(opts =>
            {
                opts.IncludeFormattedMessage = true;
                opts.IncludeScopes = true;
                opts.AddProcessor(new OpenTelemetryLoggingPrivacyProcessor());
                opts.AddProcessor(new SimpleLogRecordExportProcessor(new TestSnapshotExporter(exported)));
            });
        });
        return (factory, exported);
    }

    [Fact]
    public void OpenTelemetryPipeline_WhenRecordHasDenylistedAttributeKeys_ShouldStripDenylistedKeys()
    {
        (ILoggerFactory factory, List<ExportedRecordSnapshot> exported) = CreateTestLoggingPipeline();
        using (factory)
        {
            ILogger logger = factory.CreateLogger("PrivacyTest");

            logger.LogInformation(
                "Completed: AssetId={AssetId}, Password={Password}, CurrentPassword={CurrentPassword}, Secret={Secret}, Token={Token}, ApiKey={ApiKey}, StripePayload={StripePayload}, StorageKey={StorageKey}, ObjectPath={ObjectPath}, Prompt={Prompt}, SafeCount={SafeCount}",
                Guid.NewGuid(),
                "supersecret123",
                "p@ss",
                "topsecret",
                "abc123token",
                "api_xyz",
                "{\"id\":\"evt_123\"}",
                "dev/seed/123/456.zip",
                "assets/123/v1.bin",
                "system prompt instructions",
                42);
        }

        exported.Should().HaveCount(1);
        ExportedRecordSnapshot record = exported[0];
        var remainingKeys = record.Attributes.Select(a => a.Key).ToList();

        remainingKeys.Should().Contain("AssetId");
        remainingKeys.Should().Contain("SafeCount");

        remainingKeys.Should().NotContain(k => k.Equals("password", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("CurrentPassword", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("Secret", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("Token", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("ApiKey", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("StripePayload", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("StorageKey", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("ObjectPath", StringComparison.OrdinalIgnoreCase));
        remainingKeys.Should().NotContain(k => k.Equals("Prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpenTelemetryPipeline_WhenRecordHasSensitiveValuesInGenericKeys_ShouldStripSensitiveValues()
    {
        (ILoggerFactory factory, List<ExportedRecordSnapshot> exported) = CreateTestLoggingPipeline();
        using (factory)
        {
            ILogger logger = factory.CreateLogger("PrivacyTest");

            logger.LogInformation(
                "Diagnostic: SafeId={SafeId}, GenericString={GenericString}, AuthHeader={AuthHeader}, PathField={PathField}",
                Guid.NewGuid().ToString(),
                "sk_test_51Mz00000000000000000000000000",
                "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
                "assets/11111111222233334444555566667777/22222222333344445555666677778888.zip");
        }

        exported.Should().HaveCount(1);
        ExportedRecordSnapshot record = exported[0];
        var remainingKeys = record.Attributes.Select(a => a.Key).ToList();

        remainingKeys.Should().Contain("SafeId");
        remainingKeys.Should().NotContain("GenericString");
        remainingKeys.Should().NotContain("AuthHeader");
        remainingKeys.Should().NotContain("PathField");
    }

    [Fact]
    public void OpenTelemetryPipeline_WhenMessageContainsPasswordsTokensOrPaths_ShouldSanitizeMessageAndBody()
    {
        (ILoggerFactory factory, List<ExportedRecordSnapshot> exported) = CreateTestLoggingPipeline();
        using (factory)
        {
            ILogger logger = factory.CreateLogger("PrivacyTest");

            logger.LogInformation("Dev admin seeded -> email: admin@admin.com, password: testpassword123, auth: Bearer mySecretTokenValue, path: dev/seed/11111111222233334444555566667777/v1.zip");
            logger.LogInformation("Stripe event received with key sk_test_51Mz00000000000000000000000000");
        }

        exported.Should().HaveCount(2);
        ExportedRecordSnapshot record1 = exported[0];
        ExportedRecordSnapshot record2 = exported[1];

        record1.FormattedMessage.Should().NotContain("testpassword123");
        record1.FormattedMessage.Should().Contain("password: [REDACTED]");
        record1.FormattedMessage.Should().NotContain("mySecretTokenValue");
        record1.FormattedMessage.Should().Contain("Bearer [REDACTED_TOKEN]");
        record1.FormattedMessage.Should().NotContain("dev/seed/11111111222233334444555566667777/v1.zip");

        record2.FormattedMessage.Should().NotContain("sk_test_51Mz00000000000000000000000000");
        record2.FormattedMessage.Should().Contain("[REDACTED_STRIPE_KEY]");
    }
}
