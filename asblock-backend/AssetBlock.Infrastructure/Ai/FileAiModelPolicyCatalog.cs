using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class FileAiModelPolicyCatalog : IAiModelPolicyCatalog
{
    private readonly Dictionary<(AiProviderKind Provider, string ModelId), AiModelPolicyEntry> _entries = new();

    public FileAiModelPolicyCatalog(IHostEnvironment environment, IConfiguration configuration)
    {
        SchemaVersion = AiPromptPolicies.MODEL_POLICY_SCHEMA_VERSION_NUMBER;
        if (!AiConfigurationRules.IsEnabled(configuration))
        {
            return;
        }

        var relative = configuration[$"{AiOptions.SECTION_NAME}:ModelPolicyPath"];
        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = AiPromptPolicies.DEFAULT_MODEL_POLICY_PATH;
        }

        var path = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(environment.ContentRootPath, relative);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException("AI model policy file is required when Ai:Enabled is true.");
        }

        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<AiModelPolicyFile>(stream, _serializerOptions)
            ?? throw new InvalidOperationException("AI model policy file is empty.");

        if (document.SchemaVersion != AiPromptPolicies.MODEL_POLICY_SCHEMA_VERSION_NUMBER)
        {
            throw new InvalidOperationException("AI model policy schemaVersion is unsupported.");
        }

        if (!string.Equals(document.PolicyVersion, AiPromptPolicies.LISTING_COPILOT_V1, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AI model policy policyVersion must match the listing copilot prompt policy.");
        }

        SchemaVersion = document.SchemaVersion;
        foreach (var raw in document.Entries)
        {
            var entry = ParseEntry(raw);
            var key = (entry.Provider, entry.ModelId);
            if (!_entries.TryAdd(key, entry))
            {
                throw new InvalidOperationException("AI model policy contains duplicate model entries.");
            }
        }
    }

    public int SchemaVersion { get; }

    public bool TryGet(
        AiProviderKind provider,
        string modelId,
        [NotNullWhen(true)] out AiModelPolicyEntry? entry) =>
        _entries.TryGetValue((provider, modelId), out entry);

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static AiModelPolicyEntry ParseEntry(AiModelPolicyFileEntry raw)
    {
        if (!AiProviderParser.TryParse(raw.Provider, out var provider))
        {
            throw new InvalidOperationException("AI model policy entry has an unknown provider.");
        }

        if (!AiConfigurationRules.IsModelId(raw.ModelId))
        {
            throw new InvalidOperationException("AI model policy entry has an invalid modelId.");
        }

        if (!Enum.TryParse<AiModelUseCase>(raw.UseCase, ignoreCase: false, out var useCase)
            || useCase != AiModelUseCase.LISTING_COPILOT)
        {
            throw new InvalidOperationException("AI model policy entry has an invalid useCase.");
        }

        if (!Enum.TryParse<AiPrivacyDecision>(raw.Privacy, ignoreCase: false, out var privacy))
        {
            throw new InvalidOperationException("AI model policy entry has an invalid privacy decision.");
        }

        if (raw.MaxInputChars is < OpenRouterOptions.MIN_INPUT_CHARS or > OpenRouterOptions.MAX_INPUT_CHARS
            || raw.MaxOutputTokens is < OpenRouterOptions.MIN_OUTPUT_TOKENS or > OpenRouterOptions.MAX_OUTPUT_TOKENS)
        {
            throw new InvalidOperationException("AI model policy entry has invalid limits.");
        }

        if (string.IsNullOrWhiteSpace(raw.LicenseNote)
            || raw.LicenseNote.Length > ListingSuggestionBounds.LICENSE_NOTE_MAX_LENGTH)
        {
            throw new InvalidOperationException("AI model policy entry has an invalid licenseNote.");
        }

        if (!DateOnly.TryParse(raw.ReviewedOn, CultureInfo.InvariantCulture, DateTimeStyles.None, out var reviewedOn))
        {
            throw new InvalidOperationException("AI model policy entry has an invalid reviewedOn date.");
        }

        string? digest = null;
        if (provider == AiProviderKind.OLLAMA)
        {
            if (!AiConfigurationRules.IsSha256Digest(raw.Digest))
            {
                throw new InvalidOperationException("Ollama model policy entries require an exact sha256 digest.");
            }

            digest = raw.Digest;
        }
        else if (!string.IsNullOrWhiteSpace(raw.Digest))
        {
            throw new InvalidOperationException("OpenRouter model policy entries must not include a digest.");
        }

        return new AiModelPolicyEntry(
            provider,
            raw.ModelId,
            useCase,
            raw.StructuredOutput,
            privacy,
            raw.MaxInputChars,
            raw.MaxOutputTokens,
            raw.LicenseNote,
            reviewedOn,
            digest);
    }

    private sealed class AiModelPolicyFile
    {
        public int SchemaVersion { get; set; }
        public string PolicyVersion { get; set; } = string.Empty;
        public List<AiModelPolicyFileEntry> Entries { get; set; } = [];
    }

    private sealed class AiModelPolicyFileEntry
    {
        public string Provider { get; set; } = string.Empty;
        public string ModelId { get; set; } = string.Empty;
        public string UseCase { get; set; } = string.Empty;
        public bool StructuredOutput { get; set; }
        public string Privacy { get; set; } = string.Empty;
        public int MaxInputChars { get; set; }
        public int MaxOutputTokens { get; set; }
        public string LicenseNote { get; set; } = string.Empty;
        public string ReviewedOn { get; set; } = string.Empty;
        public string? Digest { get; set; }
    }
}
