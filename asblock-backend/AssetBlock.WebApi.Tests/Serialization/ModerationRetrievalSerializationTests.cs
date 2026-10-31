using System.Text.Json;
using AssetBlock.Domain.Core.Dto.Moderation;
using AwesomeAssertions;

namespace AssetBlock.WebApi.Tests.Serialization;

public sealed class ModerationRetrievalSerializationTests
{
    private static readonly JsonSerializerOptions _web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Serialize_WhenScoresWereNotPersisted_ShouldSayUnavailableWithoutZeroValues()
    {
        var candidate = new ModeratorRetrievalCandidate("finding", "own.py", 0, 10, 1, 1,
            "private-reference", "FOREIGN", "PRIVATE", "SCORES_UNAVAILABLE", null);
        var json = JsonSerializer.Serialize(candidate, _web);
        json.Should().Contain("\"scoresAvailability\":\"SCORES_UNAVAILABLE\"");
        json.Should().Contain("\"scores\":null");
        json.Should().NotContain("tokenScore");
        json.Should().NotContain("semanticScore");
    }

}
