using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

public sealed class OllamaTextEmbeddingGenerator : ITextEmbeddingGenerator
{
    public const string HTTP_CLIENT_NAME = "OllamaTextEmbeddingGenerator";

    private readonly HttpClient _httpClient;
    private readonly IOptions<EmbeddingOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _verificationLock = new();
    private long _lastVerifiedTimestamp;
    private bool _hasVerified;
    private string? _lastVerifiedModel;
    private string? _lastVerifiedDigest;
    private static readonly TimeSpan _verificationCacheDuration = TimeSpan.FromSeconds(30);

    public OllamaTextEmbeddingGenerator(
        HttpClient httpClient,
        IOptions<EmbeddingOptions> options,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.Value.Enabled)
        {
            if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(_options.Value.BaseUrl, allowHttps: false, requireLoopback: true))
            {
                throw new InvalidOperationException($"BaseUrl '{_options.Value.BaseUrl}' must be an absolute loopback HTTP URL.");
            }
        }
    }

    public async Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken = default)
    {
        EmbeddingOptions options = _options.Value;
        if (!options.Enabled)
        {
            return new ModelVerificationResult(false, "Embeddings are disabled by configuration.");
        }

        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            return new ModelVerificationResult(false, $"BaseUrl '{options.BaseUrl}' is not a valid loopback HTTP URL.");
        }

        lock (_verificationLock)
        {
            if (_hasVerified
                && _lastVerifiedModel == options.Model
                && _lastVerifiedDigest == options.Digest
                && _timeProvider.GetElapsedTime(_lastVerifiedTimestamp) < _verificationCacheDuration)
            {
                return new ModelVerificationResult(true, null, _lastVerifiedDigest);
            }
        }

        try
        {
            var requestUri = new Uri(new Uri(options.BaseUrl), "/api/tags");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                lock (_verificationLock)
                { _hasVerified = false; }
                return new ModelVerificationResult(false, $"Local Ollama returned HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out JsonElement modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
            {
                lock (_verificationLock)
                { _hasVerified = false; }
                return new ModelVerificationResult(false, "Malformed response from local Ollama: missing 'models' array.");
            }

            JsonElement? matchedModel = null;
            foreach (JsonElement m in modelsElement.EnumerateArray())
            {
                if (m.TryGetProperty("name", out JsonElement nameProp)
                    && string.Equals(nameProp.GetString(), options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    matchedModel = m;
                    break;
                }
                if (m.TryGetProperty("model", out JsonElement modelProp)
                    && string.Equals(modelProp.GetString(), options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    matchedModel = m;
                    break;
                }
            }

            if (matchedModel is null)
            {
                lock (_verificationLock)
                { _hasVerified = false; }
                return new ModelVerificationResult(
                    false,
                    $"Candidate model '{options.Model}' is not installed in local Ollama.");
            }

            string? actualDigest = null;
            if (matchedModel.Value.TryGetProperty("digest", out JsonElement digestProp))
            {
                var rawDigest = digestProp.GetString();
                if (!string.IsNullOrWhiteSpace(rawDigest))
                {
                    actualDigest = rawDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? rawDigest.ToLowerInvariant()
                        : $"sha256:{rawDigest.ToLowerInvariant()}";
                }
            }

            if (string.IsNullOrWhiteSpace(actualDigest))
            {
                lock (_verificationLock)
                { _hasVerified = false; }
                return new ModelVerificationResult(
                    false,
                    $"Local Ollama model '{options.Model}' does not expose a valid SHA-256 digest in /api/tags.");
            }

            if (!string.Equals(actualDigest, options.Digest, StringComparison.OrdinalIgnoreCase))
            {
                lock (_verificationLock)
                { _hasVerified = false; }
                return new ModelVerificationResult(
                    false,
                    $"Model digest mismatch for '{options.Model}': configured '{options.Digest}', but local Ollama has '{actualDigest}'.",
                    actualDigest);
            }

            lock (_verificationLock)
            {
                _lastVerifiedTimestamp = _timeProvider.GetTimestamp();
                _lastVerifiedModel = options.Model;
                _lastVerifiedDigest = actualDigest;
                _hasVerified = true;
            }

            return new ModelVerificationResult(true, null, actualDigest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException)
        {
            lock (_verificationLock)
            { _hasVerified = false; }
            return new ModelVerificationResult(false, $"Connection to local Ollama daemon timed out at '{options.BaseUrl}'.");
        }
        catch (HttpRequestException ex)
        {
            lock (_verificationLock)
            { _hasVerified = false; }
            var status = ex.StatusCode.HasValue ? $" HTTP {(int)ex.StatusCode.Value}" : string.Empty;
            return new ModelVerificationResult(false, $"Local Ollama daemon is unreachable at '{options.BaseUrl}'{status}.");
        }
        catch (Exception)
        {
            lock (_verificationLock)
            { _hasVerified = false; }
            return new ModelVerificationResult(false, "Unexpected error communicating with local Ollama daemon.");
        }
    }

    public async Task<GeneratedEmbedding> Generate(string text, CancellationToken cancellationToken = default)
    {
        EmbeddingOptions options = _options.Value;
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Embeddings are disabled by configuration.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Input text cannot be empty or whitespace.", nameof(text));
        }

        if (text.Length > options.MaxInputChars)
        {
            throw new ArgumentException($"Input text exceeds max length {options.MaxInputChars}.", nameof(text));
        }

        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            throw new InvalidOperationException($"BaseUrl '{options.BaseUrl}' must be an absolute loopback HTTP URL.");
        }

        var requestUri = new Uri(new Uri(options.BaseUrl), "/api/embed");
        var requestPayload = new
        {
            model = options.Model,
            input = text
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(requestUri, requestPayload, cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Local Ollama embedding request timed out after {options.RequestTimeoutSeconds}s.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"Local Ollama returned HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("embeddings", out JsonElement embeddingsElement)
                || embeddingsElement.ValueKind != JsonValueKind.Array
                || embeddingsElement.GetArrayLength() != 1
                || embeddingsElement[0].ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Malformed response from local Ollama: expected exactly one vector in 'embeddings'.");
            }

            JsonElement embeddingElement = embeddingsElement[0];
            var vectorLength = embeddingElement.GetArrayLength();
            var vector = new float[vectorLength];
            var index = 0;

            foreach (JsonElement item in embeddingElement.EnumerateArray())
            {
                vector[index++] = item.GetSingle();
            }

            VectorValidation.Validate(vector, options.Dimension);

            var modelKey = EmbeddingModelKey.Compute(options);
            return new GeneratedEmbedding(vector, modelKey, options.Dimension);
        }
    }
}
