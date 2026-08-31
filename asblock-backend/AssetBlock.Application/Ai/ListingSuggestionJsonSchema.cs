using System.Text.Json.Nodes;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.Ai;

internal static class ListingSuggestionJsonSchema
{
    public static string ForAllowlists(IReadOnlyList<string> categories, IReadOnlyList<string> tags)
    {
        var categoryEnum = new JsonArray();
        foreach (var category in categories.Distinct(StringComparer.Ordinal))
        {
            categoryEnum.Add(category);
        }

        var tagItems = new JsonObject { ["type"] = "string" };
        var distinctTags = tags.Distinct(StringComparer.Ordinal).ToList();
        if (distinctTags.Count > 0)
        {
            var tagEnum = new JsonArray();
            foreach (var tag in distinctTags)
            {
                tagEnum.Add(tag);
            }

            tagItems["enum"] = tagEnum;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("title", "description", "category", "tags"),
            ["properties"] = new JsonObject
            {
                ["title"] = new JsonObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = ListingSuggestionBounds.TITLE_MAX_LENGTH
                },
                ["description"] = new JsonObject
                {
                    ["type"] = "string",
                    ["maxLength"] = ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH
                },
                ["category"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = categoryEnum
                },
                ["tags"] = new JsonObject
                {
                    ["type"] = "array",
                    ["uniqueItems"] = true,
                    ["maxItems"] = Math.Min(ListingSuggestionBounds.MAX_SUGGESTED_TAGS, Math.Max(distinctTags.Count, 0)),
                    ["items"] = tagItems
                }
            }
        };

        return schema.ToJsonString();
    }
}
