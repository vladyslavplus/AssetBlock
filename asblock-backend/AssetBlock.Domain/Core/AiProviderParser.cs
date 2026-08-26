using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core;

public static class AiProviderParser
{
    public static bool TryParse(string? value, out AiProviderKind kind)
    {
        if (string.Equals(value, "OpenRouter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, nameof(AiProviderKind.OPENROUTER), StringComparison.OrdinalIgnoreCase))
        {
            kind = AiProviderKind.OPENROUTER;
            return true;
        }

        if (string.Equals(value, "Ollama", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, nameof(AiProviderKind.OLLAMA), StringComparison.OrdinalIgnoreCase))
        {
            kind = AiProviderKind.OLLAMA;
            return true;
        }

        kind = default;
        return false;
    }
}
