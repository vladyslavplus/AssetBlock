namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class ElasticsearchOptions
{
    public const string SECTION_NAME = "Elasticsearch";

    public string Url { get; set; } = "http://localhost:9200";
    public string DefaultIndex { get; set; } = "assets";
}
