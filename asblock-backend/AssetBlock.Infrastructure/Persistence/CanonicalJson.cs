using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetBlock.Infrastructure.Persistence;

/// <summary>
/// Canonical JSON encoding that matches the analysis service byte for byte: object keys sorted by
/// ordinal name, UTF-8 without ASCII escaping, compact separators, a single terminal LF, and
/// numbers emitted from their original token text.
///
/// The encoding is built directly instead of through a JSON writer, because no built-in writer can
/// emit non-ASCII property names and values unescaped while still accepting raw numeric tokens.
/// It is for internal digest computation only and must never be used for HTML or other output.
/// </summary>
internal static partial class CanonicalJson
{
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex JsonNumberRegex();

    public static byte[] Canonicalize(JsonElement element)
    {
        var builder = new StringBuilder(4096);
        WriteElement(builder, element);
        builder.Append('\n');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public static string CanonicalizeToString(JsonElement element)
    {
        var builder = new StringBuilder(4096);
        WriteElement(builder, element);
        builder.Append('\n');
        return builder.ToString();
    }

    public static string Sha256Hex(JsonElement element) =>
        Sha256HexOfBytes(Canonicalize(element));

    public static string Sha256Hex(string utf8Text) =>
        Sha256HexOfBytes(Encoding.UTF8.GetBytes(utf8Text));

    /// <summary>
    /// Canonical digest over a composed value tree. Only the primitive shapes the product digests
    /// need are supported, and numeric values are emitted from invariant round-trip text so no
    /// value can be silently rewritten.
    /// </summary>
    public static string DigestOf(CanonicalValue value)
    {
        var builder = new StringBuilder(4096);
        WriteValue(builder, value);
        builder.Append('\n');
        return Sha256HexOfBytes(Encoding.UTF8.GetBytes(builder.ToString()));
    }

    private static string Sha256HexOfBytes(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static void WriteElement(StringBuilder builder, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new List<string>();
                var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    // A duplicate member would make the canonical form ambiguous, so it is rejected
                    // rather than silently collapsing into the last occurrence.
                    if (!values.TryAdd(property.Name, property.Value))
                    {
                        throw new CanonicalJsonException($"Duplicate JSON property '{property.Name}'.");
                    }

                    names.Add(property.Name);
                }

                names.Sort(CompareByCodePoint);
                builder.Append('{');
                for (var i = 0; i < names.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(builder, names[i]);
                    builder.Append(':');
                    WriteElement(builder, values[names[i]]);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    WriteElement(builder, item);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                WriteString(builder, RequireEncodableString(element.GetString()));
                break;

            case JsonValueKind.Number:
                WriteNumberToken(builder, element.GetRawText());
                break;

            case JsonValueKind.True:
                builder.Append("true");
                break;

            case JsonValueKind.False:
                builder.Append("false");
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                throw new CanonicalJsonException($"Unsupported JSON value kind: {element.ValueKind}.");
        }
    }

    private static void WriteValue(StringBuilder builder, CanonicalValue value)
    {
        switch (value.Kind)
        {
            case CanonicalValueKind.NULL:
                builder.Append("null");
                break;

            case CanonicalValueKind.STRING:
                WriteString(builder, RequireEncodableString(value.ScalarText));
                break;

            case CanonicalValueKind.BOOL:
                builder.Append(value.ScalarBool ? "true" : "false");
                break;

            case CanonicalValueKind.NUMBER:
                WriteNumberToken(builder, value.ScalarText);
                break;

            case CanonicalValueKind.ARRAY:
                builder.Append('[');
                for (var i = 0; i < value.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteValue(builder, value.Items[i]);
                }

                builder.Append(']');
                break;

            case CanonicalValueKind.OBJECT:
                builder.Append('{');
                for (var i = 0; i < value.Members.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteString(builder, value.Members[i].Key);
                    builder.Append(':');
                    WriteValue(builder, value.Members[i].Value);
                }

                builder.Append('}');
                break;

            default:
                throw new InvalidOperationException($"Unsupported canonical value kind: {value.Kind}.");
        }
    }

    /// <summary>
    /// Writes the original number token. Anything outside the JSON number grammar is rejected so a
    /// malformed or non-finite token can never be normalized into something that still digests.
    /// </summary>
    private static void WriteNumberToken(StringBuilder builder, string token)
    {
        if (!JsonNumberRegex().IsMatch(token))
        {
            throw new CanonicalJsonException($"'{token}' is not a canonical JSON number.");
        }

        builder.Append(token);
    }

    /// <summary>
    /// Writes a quoted string, escaping exactly what the service canonical form escapes: the quote,
    /// the backslash and the C0 control range. Everything else, including non-BMP and
    /// HTML-sensitive characters, stays literal so both languages emit identical UTF-8 bytes.
    /// </summary>
    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Orders member names by Unicode code point, which is what the service canonical form does.
    /// An ordinal UTF-16 comparison would disagree: it sorts a non-BMP name by its leading
    /// surrogate and so places U+10000 before U+E000, while code-point order is the reverse.
    /// UTF-8 byte order equals code-point order, so comparing the encoded forms is exact.
    /// </summary>
    private static int CompareByCodePoint(string left, string right) =>
        Encoding.UTF8.GetBytes(left).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// Rejects unpaired surrogates, which have no UTF-8 encoding and would otherwise be replaced
    /// silently during canonicalization.
    /// </summary>
    private static string RequireEncodableString(string? value)
    {
        if (value is null)
        {
            throw new CanonicalJsonException("A JSON string value must not be null.");
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    throw new CanonicalJsonException("A JSON string contains an unpaired high surrogate.");
                }

                i++;
            }
            else if (char.IsLowSurrogate(value[i]))
            {
                throw new CanonicalJsonException("A JSON string contains an unpaired low surrogate.");
            }
        }

        return value;
    }
}

/// <summary>Fail-closed signal for input that cannot be canonicalized without changing its meaning.</summary>
internal sealed class CanonicalJsonException(string message) : Exception(message);
