using System.Text.Json;

namespace AssetBlock.Infrastructure.Ai;

internal static class JsonSchemaContractValidator
{
    private static readonly HashSet<string> _supportedKeywords =
    [
        "type",
        "enum",
        "required",
        "properties",
        "additionalProperties",
        "maxItems",
        "uniqueItems",
        "items",
        "minLength",
        "maxLength"
    ];

    public static bool IsValid(string json, string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            using var schema = JsonDocument.Parse(schemaJson);
            return Matches(document.RootElement, schema.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Matches(JsonElement value, JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (schema.EnumerateObject().Any(property => !_supportedKeywords.Contains(property.Name)))
        {
            return false;
        }

        if (schema.TryGetProperty("type", out JsonElement type)
            && (type.ValueKind != JsonValueKind.String || !MatchesType(value, type.GetString())))
        {
            return false;
        }

        if (schema.TryGetProperty("enum", out JsonElement allowed)
            && (allowed.ValueKind != JsonValueKind.Array
                || !allowed.EnumerateArray().Any(candidate => JsonElement.DeepEquals(candidate, value))))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Object => MatchesObject(value, schema),
            JsonValueKind.Array => MatchesArray(value, schema),
            JsonValueKind.String => MatchesString(value.GetString()!, schema),
            _ => true
        };
    }

    private static bool MatchesObject(JsonElement value, JsonElement schema)
    {
        if (schema.TryGetProperty("required", out JsonElement required)
            && (required.ValueKind != JsonValueKind.Array
                || required.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String
                    || !value.TryGetProperty(item.GetString()!, out _))))
        {
            return false;
        }

        var hasProperties = schema.TryGetProperty("properties", out JsonElement properties);
        if (hasProperties && properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var disallowAdditional = schema.TryGetProperty("additionalProperties", out JsonElement additional)
            && additional.ValueKind == JsonValueKind.False;
        if (additional.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!hasProperties || !properties.TryGetProperty(property.Name, out JsonElement propertySchema))
            {
                if (disallowAdditional)
                {
                    return false;
                }

                continue;
            }

            if (!Matches(property.Value, propertySchema))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesArray(JsonElement value, JsonElement schema)
    {
        var length = value.GetArrayLength();
        if (schema.TryGetProperty("maxItems", out JsonElement maxItems)
            && (!maxItems.TryGetInt32(out var maximum) || length > maximum))
        {
            return false;
        }

        if (schema.TryGetProperty("uniqueItems", out JsonElement uniqueItems)
            && uniqueItems.ValueKind == JsonValueKind.True)
        {
            JsonElement[] items = value.EnumerateArray().ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                if (items.Skip(i + 1).Any(candidate => JsonElement.DeepEquals(items[i], candidate)))
                {
                    return false;
                }
            }
        }

        if (!schema.TryGetProperty("items", out JsonElement itemSchema))
        {
            return true;
        }

        return itemSchema.ValueKind == JsonValueKind.Object
            && value.EnumerateArray().All(item => Matches(item, itemSchema));
    }

    private static bool MatchesString(string value, JsonElement schema)
    {
        if (schema.TryGetProperty("minLength", out JsonElement minLength)
            && (!minLength.TryGetInt32(out var minimum) || value.Length < minimum))
        {
            return false;
        }

        return !schema.TryGetProperty("maxLength", out JsonElement maxLength)
            || (maxLength.TryGetInt32(out var maximum) && value.Length <= maximum);
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => false
    };
}
