using System.Text.RegularExpressions;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace AssetBlock.WebApi.Observability;

/// <summary>
/// Centralized OpenTelemetry log processor that sanitizes log records before export via OTLP.
/// Ensures that passwords, secrets, tokens, storage credentials/keys, full object paths, seller-controlled
/// manifest content/paths, request bodies, prompts, raw AI output, query secrets, and JWT/Stripe payloads
/// cannot leave the process boundary.
/// </summary>
public sealed class OpenTelemetryLoggingPrivacyProcessor : BaseProcessor<LogRecord>
{
    private static readonly HashSet<string> _exactDenylistKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "currentpassword",
        "newpassword",
        "secret",
        "token",
        "refreshtoken",
        "accesstoken",
        "hubtoken",
        "bearertoken",
        "apikey",
        "api_key",
        "credential",
        "credentials",
        "authorization",
        "jwt",
        "stripe",
        "stripepayload",
        "stripesignature",
        "prompt",
        "systemprompt",
        "userprompt",
        "readme",
        "readmecontent",
        "manifest",
        "manifests",
        "manifestmetadata",
        "manifestcontent",
        "manifestpath",
        "storagekey",
        "storage_key",
        "objectpath",
        "blobkey",
        "blobpath",
        "fullpath",
        "requestbody",
        "responsebody",
        "rawoutput",
        "aioutput",
        "rawresponse",
        "cookie",
        "setcookie",
        "privatekey",
        "certpassword",
        "certificatepassword",
        "querysecret",
        "payload"
    };

    private static readonly string[] _denylistKeySubstrings =
    [
        "password",
        "secret",
        "apikey",
        "api_key",
        "token",
        "credential",
        "privatekey",
        "storagekey",
        "storage_key",
        "objectpath",
        "blobkey",
        "manifestcontent",
        "manifestmetadata",
        "readmecontent",
        "requestbody",
        "responsebody",
        "rawoutput",
        "aioutput",
        "rawresponse",
        "stripesignature",
        "stripepayload"
    ];

    private static readonly Regex _jwtRegex = new(
        @"\b[A-Za-z0-9-_]{16,}\.[A-Za-z0-9-_]{16,}\.[A-Za-z0-9-_]{16,}\b",
        RegexOptions.Compiled);

    private static readonly Regex _bearerRegex = new(
        @"(?i)Bearer\s+[A-Za-z0-9-_.~+/]+=*",
        RegexOptions.Compiled);

    private static readonly Regex _stripeKeyRegex = new(
        @"\b(?:sk|rk|whsec)_(?:test|live)_[0-9a-zA-Z]{16,}\b",
        RegexOptions.Compiled);

    private static readonly Regex _passwordAssignmentRegex = new(
        @"(?i)(password\s*[:=]\s*)([^\s,;}]+)",
        RegexOptions.Compiled);

    private static readonly Regex _storagePathRegex = new(
        @"(?i)\b(?:assets|dev/seed)/[0-9a-fA-F-]{16,}(?:/[0-9a-fA-F-]{16,})*(?:\.[a-zA-Z0-9]+)?\b",
        RegexOptions.Compiled);

    public override void OnEnd(LogRecord data)
    {
        // 1. Sanitize structured attributes
        if (data.Attributes is { Count: > 0 })
        {
            var sanitized = new List<KeyValuePair<string, object?>>(data.Attributes.Count);
            foreach (KeyValuePair<string, object?> attr in data.Attributes)
            {
                if (IsDenylistedKey(attr.Key))
                {
                    continue;
                }

                if (attr.Value is string stringVal)
                {
                    if (IsSensitiveValue(stringVal))
                    {
                        continue;
                    }

                    var cleaned = SanitizeText(stringVal);
                    sanitized.Add(new KeyValuePair<string, object?>(attr.Key, cleaned));
                }
                else
                {
                    sanitized.Add(attr);
                }
            }

            data.Attributes = sanitized;
        }

        // 2. Sanitize formatted message and body
        if (!string.IsNullOrEmpty(data.FormattedMessage))
        {
            data.FormattedMessage = SanitizeText(data.FormattedMessage);
        }

        if (!string.IsNullOrEmpty(data.Body))
        {
            data.Body = SanitizeText(data.Body);
        }
    }

    private static bool IsDenylistedKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim();
        if (_exactDenylistKeys.Contains(normalized))
        {
            return true;
        }

        foreach (var sub in _denylistKeySubstrings)
        {
            if (normalized.Contains(sub, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSensitiveValue(string val)
    {
        if (string.IsNullOrWhiteSpace(val))
        {
            return false;
        }

        var trimmed = val.Trim();
        if (_jwtRegex.IsMatch(trimmed))
        {
            return true;
        }

        if (_bearerRegex.IsMatch(trimmed))
        {
            return true;
        }

        if (_stripeKeyRegex.IsMatch(trimmed))
        {
            return true;
        }

        if (_storagePathRegex.IsMatch(trimmed))
        {
            return true;
        }

        return false;
    }

    public static string SanitizeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var result = text;
        if (_passwordAssignmentRegex.IsMatch(result))
        {
            result = _passwordAssignmentRegex.Replace(result, "$1[REDACTED]");
        }

        if (_bearerRegex.IsMatch(result))
        {
            result = _bearerRegex.Replace(result, "Bearer [REDACTED_TOKEN]");
        }

        if (_stripeKeyRegex.IsMatch(result))
        {
            result = _stripeKeyRegex.Replace(result, "[REDACTED_STRIPE_KEY]");
        }

        if (_jwtRegex.IsMatch(result))
        {
            result = _jwtRegex.Replace(result, "[REDACTED_JWT]");
        }

        if (_storagePathRegex.IsMatch(result))
        {
            result = _storagePathRegex.Replace(result, "[REDACTED_STORAGE_PATH]");
        }

        return result;
    }
}
