using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Dto.Paging;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SortDirection
{
    ASC,
    DESC
}
