using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace AssetBlock.Infrastructure.Ai;

internal static partial class AiConfigurationRules
{
    private static readonly Regex _modelIdPattern = MyRegex();
    private static readonly Regex _policyVersionPattern = MyRegex1();
    private static readonly Regex _appNamePattern = MyRegex2();
    private static readonly Regex _digestPattern = MyRegex3();

    private static bool IsEnabled(IConfiguration configuration) =>
        configuration.GetValue($"{AiOptions.SECTION_NAME}:Enabled", false);

    public static bool IsActiveProvider(IConfiguration configuration, AiProviderKind kind)
    {
        if (!IsEnabled(configuration))
        {
            return false;
        }

        return AiProviderParser.TryParse(configuration[$"{AiOptions.SECTION_NAME}:Provider"], out var parsed)
            && parsed == kind;
    }

    public static bool IsModelId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= ListingSuggestionBounds.MODEL_ID_MIN_LENGTH and <= ListingSuggestionBounds.MODEL_ID_MAX_LENGTH
        && _modelIdPattern.IsMatch(value);

    public static bool IsPolicyVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= AiPromptPolicies.POLICY_VERSION_MIN_LENGTH and <= AiPromptPolicies.POLICY_VERSION_MAX_LENGTH
        && _policyVersionPattern.IsMatch(value);

    public static bool IsAppName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= ListingSuggestionBounds.APP_NAME_MAX_LENGTH
        && _appNamePattern.IsMatch(value);

    public static bool IsSha256Digest(string? value) =>
        !string.IsNullOrWhiteSpace(value) && _digestPattern.IsMatch(value);

    public static bool IsAbsoluteHttpOrHttps(string? value, bool allowHttps, bool requireLoopback)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            return !requireLoopback || uri.IsLoopback;
        }

        return allowHttps && uri.Scheme == Uri.UriSchemeHttps && (!requireLoopback || uri.IsLoopback);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,199}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled)]
    private static partial Regex MyRegex1();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex2();
    [GeneratedRegex("^sha256:[a-fA-F0-9]{64}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex3();
}
