namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

/// <summary>
/// Model and runtime configuration for semantic embeddings.
/// Tracked default has Enabled = false, empty model/revision/digest, and Dimension = 0.
/// appsettings is the sole configuration source (no model-policy.json, no cloud fallback).
/// </summary>
public sealed class EmbeddingOptions
{
    private const string SECTION_NAME = "Embeddings";
    public const string CONFIGURATION_PATH = AiOptions.SECTION_NAME + ":" + SECTION_NAME;

    public const int MIN_MODEL_LENGTH = 3;
    public const int MAX_MODEL_LENGTH = 200;
    public const int MIN_REVISION_LENGTH = 1;
    public const int MAX_REVISION_LENGTH = 200;
    public const int MIN_DIMENSION = 1;
    public const int MAX_DIMENSION = 16_000;
    public const int MIN_REQUEST_TIMEOUT_SECONDS = 1;
    public const int MAX_REQUEST_TIMEOUT_SECONDS = 300;
    public const int MIN_QUERY_TIMEOUT_MILLISECONDS = 50;
    public const int MAX_QUERY_TIMEOUT_MILLISECONDS = 10_000;
    public const int MIN_INPUT_CHARS = 100;
    public const int MAX_INPUT_CHARS = 32_768;
    public const int MIN_BACKFILL_BATCH_SIZE = 1;
    public const int MAX_BACKFILL_BATCH_SIZE = 100;
    public const int MIN_BACKFILL_POLL_SECONDS = 1;
    public const int MAX_BACKFILL_POLL_SECONDS = 3600;

    public bool Enabled { get; set; }
    public string Provider { get; set; } = "Ollama";
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = string.Empty;
    public string Revision { get; set; } = string.Empty;
    public string Digest { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string ContentSchemaVersion { get; set; } = "asset-public-metadata-v1";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int QueryTimeoutMilliseconds { get; set; } = 900;
    public int MaxInputChars { get; set; } = 8192;
    public int BackfillBatchSize { get; set; } = 50;
    public int BackfillPollSeconds { get; set; } = 30;
}
