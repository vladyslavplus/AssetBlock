using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed partial class EmbeddingOptionsValidator : IValidateOptions<EmbeddingOptions>
{
    private static readonly Regex _sha256LowerHexRegex = MyRegex();

    public ValidateOptionsResult Validate(string? name, EmbeddingOptions options)
    {
        // When disabled, tracked configuration may use empty model/revision/digest and dimension 0.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (!string.Equals(options.Provider, "Ollama", StringComparison.Ordinal))
        {
            errors.Add("Ai:Embeddings:Provider must be exactly 'Ollama' when enabled.");
        }

        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            errors.Add("Ai:Embeddings:BaseUrl must be an absolute loopback HTTP URL.");
        }

        if (!IsPinnedModelId(options.Model))
        {
            errors.Add("Ai:Embeddings:Model must be an explicit pinned model with a non-floating tag (floating tags such as ':latest' or untagged models are prohibited).");
        }

        if (string.IsNullOrWhiteSpace(options.Revision)
            || options.Revision.Length is < EmbeddingOptions.MIN_REVISION_LENGTH or > EmbeddingOptions.MAX_REVISION_LENGTH)
        {
            errors.Add($"Ai:Embeddings:Revision must be between {EmbeddingOptions.MIN_REVISION_LENGTH} and {EmbeddingOptions.MAX_REVISION_LENGTH} characters.");
        }

        if (string.IsNullOrWhiteSpace(options.Digest) || !_sha256LowerHexRegex.IsMatch(options.Digest))
        {
            errors.Add("Ai:Embeddings:Digest must be an exact 'sha256:' followed by 64 lowercase hex characters.");
        }

        if (options.Dimension is < EmbeddingOptions.MIN_DIMENSION or > EmbeddingOptions.MAX_DIMENSION)
        {
            errors.Add($"Ai:Embeddings:Dimension must be between {EmbeddingOptions.MIN_DIMENSION} and {EmbeddingOptions.MAX_DIMENSION}.");
        }

        if (!string.Equals(options.ContentSchemaVersion, "asset-public-metadata-v1", StringComparison.Ordinal))
        {
            errors.Add("Ai:Embeddings:ContentSchemaVersion must be 'asset-public-metadata-v1'.");
        }

        if (options.RequestTimeoutSeconds is < EmbeddingOptions.MIN_REQUEST_TIMEOUT_SECONDS or > EmbeddingOptions.MAX_REQUEST_TIMEOUT_SECONDS)
        {
            errors.Add($"Ai:Embeddings:RequestTimeoutSeconds must be between {EmbeddingOptions.MIN_REQUEST_TIMEOUT_SECONDS} and {EmbeddingOptions.MAX_REQUEST_TIMEOUT_SECONDS}.");
        }

        if (options.QueryTimeoutMilliseconds is < EmbeddingOptions.MIN_QUERY_TIMEOUT_MILLISECONDS or > EmbeddingOptions.MAX_QUERY_TIMEOUT_MILLISECONDS)
        {
            errors.Add($"Ai:Embeddings:QueryTimeoutMilliseconds must be between {EmbeddingOptions.MIN_QUERY_TIMEOUT_MILLISECONDS} and {EmbeddingOptions.MAX_QUERY_TIMEOUT_MILLISECONDS}.");
        }

        if (options.MaxInputChars is < EmbeddingOptions.MIN_INPUT_CHARS or > EmbeddingOptions.MAX_INPUT_CHARS)
        {
            errors.Add($"Ai:Embeddings:MaxInputChars must be between {EmbeddingOptions.MIN_INPUT_CHARS} and {EmbeddingOptions.MAX_INPUT_CHARS}.");
        }

        if (options.BackfillBatchSize is < EmbeddingOptions.MIN_BACKFILL_BATCH_SIZE or > EmbeddingOptions.MAX_BACKFILL_BATCH_SIZE)
        {
            errors.Add($"Ai:Embeddings:BackfillBatchSize must be between {EmbeddingOptions.MIN_BACKFILL_BATCH_SIZE} and {EmbeddingOptions.MAX_BACKFILL_BATCH_SIZE}.");
        }

        if (options.BackfillPollSeconds is < EmbeddingOptions.MIN_BACKFILL_POLL_SECONDS or > EmbeddingOptions.MAX_BACKFILL_POLL_SECONDS)
        {
            errors.Add($"Ai:Embeddings:BackfillPollSeconds must be between {EmbeddingOptions.MIN_BACKFILL_POLL_SECONDS} and {EmbeddingOptions.MAX_BACKFILL_POLL_SECONDS}.");
        }

        return errors.Count > 0 ? ValidateOptionsResult.Fail(errors) : ValidateOptionsResult.Success;
    }

    public static bool IsPinnedModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)
            || model.Length is < EmbeddingOptions.MIN_MODEL_LENGTH or > EmbeddingOptions.MAX_MODEL_LENGTH
            || !AiConfigurationRules.IsModelId(model))
        {
            return false;
        }

        var colonIndex = model.LastIndexOf(':');
        if (colonIndex <= 0 || colonIndex >= model.Length - 1)
        {
            return false;
        }

        var tag = model[(colonIndex + 1)..];
        return !string.Equals(tag, "latest", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
