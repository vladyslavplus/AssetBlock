using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using AssetBlock.SearchEvaluation.VectorOperations;

namespace AssetBlock.SearchEvaluation.Ollama;

public sealed class LocalOllamaEmbeddingClient : IOllamaEmbeddingClient
{
    private readonly HttpClient _httpClient;
    private readonly EmbeddingOptions _options;

    public LocalOllamaEmbeddingClient(HttpClient httpClient, EmbeddingOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

        // Enforce loopback URL safety invariant immediately
        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(_options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            throw new InvalidOperationException($"BaseUrl '{_options.BaseUrl}' must be an absolute loopback HTTP URL.");
        }
    }

    public async Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken)
    {
        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(_options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            return new ModelVerificationResult(false, $"BaseUrl '{_options.BaseUrl}' is not a valid loopback HTTP URL.", null);
        }

        try
        {
            var requestUri = new Uri(new Uri(_options.BaseUrl), "/api/tags");
            using HttpResponseMessage response = await _httpClient.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new ModelVerificationResult(false, $"Local Ollama returned HTTP {(int)response.StatusCode}: {errorBody}", null);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("models", out JsonElement modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
            {
                return new ModelVerificationResult(false, "Malformed response from local Ollama: missing 'models' array.", null);
            }

            JsonElement? matchedModel = null;
            foreach (JsonElement m in modelsElement.EnumerateArray())
            {
                if (m.TryGetProperty("name", out JsonElement nameProp)
                    && string.Equals(nameProp.GetString(), _options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    matchedModel = m;
                    break;
                }
                if (m.TryGetProperty("model", out JsonElement modelProp)
                    && string.Equals(modelProp.GetString(), _options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    matchedModel = m;
                    break;
                }
            }

            if (matchedModel is null)
            {
                return new ModelVerificationResult(
                    false,
                    $"Candidate model '{_options.Model}' is not installed in local Ollama. The evaluation runner does not pull or download models automatically. Please install it manually via 'ollama run/pull {_options.Model}'.",
                    null);
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
                return new ModelVerificationResult(
                    false,
                    $"Local Ollama model '{_options.Model}' does not expose a valid SHA-256 digest in /api/tags. Expected '{_options.Digest}'.",
                    null);
            }

            if (!string.Equals(actualDigest, _options.Digest, StringComparison.OrdinalIgnoreCase))
            {
                return new ModelVerificationResult(
                    false,
                    $"Model digest mismatch for '{_options.Model}': configured '{_options.Digest}', but local Ollama model has '{actualDigest}'.",
                    actualDigest);
            }

            return new ModelVerificationResult(true, null, actualDigest);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is TimeoutException or TaskCanceledException)
        {
            return new ModelVerificationResult(false, $"Connection to local Ollama daemon timed out at '{_options.BaseUrl}': {ex.Message}", null);
        }
        catch (HttpRequestException ex)
        {
            return new ModelVerificationResult(false, $"Local Ollama daemon is unreachable at '{_options.BaseUrl}': {ex.Message}", null);
        }
        catch (Exception ex)
        {
            return new ModelVerificationResult(false, $"Unexpected error communicating with local Ollama daemon: {ex.Message}", null);
        }
    }

    public async Task<float[]> GenerateEmbedding(string text, CancellationToken cancellationToken)
    {
        if (!AiConfigurationRules.IsAbsoluteHttpOrHttps(_options.BaseUrl, allowHttps: false, requireLoopback: true))
        {
            throw new InvalidOperationException($"BaseUrl '{_options.BaseUrl}' must be an absolute loopback HTTP URL.");
        }

        var requestUri = new Uri(new Uri(_options.BaseUrl), "/api/embed");
        var requestPayload = new
        {
            model = _options.Model,
            input = text
        };

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(requestUri, requestPayload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Local Ollama returned HTTP {(int)response.StatusCode}: {errorBody}");
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

        // Validate vector dimension, finiteness, and non-zero norm
        VectorMath.ValidateVector(vector, _options.Dimension);

        return vector;
    }
}
