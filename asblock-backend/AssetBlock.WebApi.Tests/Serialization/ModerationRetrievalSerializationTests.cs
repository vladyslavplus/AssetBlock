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
        var candidate = new ModeratorRetrievalCandidate(Guid.NewGuid(), "finding", "SIMILARITY_SEMANTIC", "own.py", 0, 10, 1, 1,
            "private-reference", "FOREIGN", "PRIVATE", "SCORES_UNAVAILABLE", null, false, false, []);
        var json = JsonSerializer.Serialize(candidate, _web);
        json.Should().Contain("\"scoresAvailability\":\"SCORES_UNAVAILABLE\"");
        json.Should().Contain("\"findingType\":\"SIMILARITY_SEMANTIC\"");
        json.Should().Contain("\"scores\":null");
        json.Should().NotContain("tokenScore");
        json.Should().NotContain("semanticScore");
    }

}
