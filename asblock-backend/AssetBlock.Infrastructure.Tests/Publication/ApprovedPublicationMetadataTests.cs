using AssetBlock.Domain.Core.Publication;

namespace AssetBlock.Infrastructure.Tests.Publication;

public sealed class ApprovedPublicationMetadataTests
{
    private static readonly Guid _categoryId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void TryReadPublicProjection_WhenTagsMissing_ShouldReturnFalse()
    {
        var json = """{"title":"T","categoryId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"}""";
        ApprovedPublicationMetadata.TryReadPublicProjection(json, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadPublicProjection_WhenTagsNull_ShouldReturnFalse()
    {
        var json = """{"title":"T","categoryId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","tags":null}""";
        ApprovedPublicationMetadata.TryReadPublicProjection(json, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadPublicProjection_WhenTagElementBlank_ShouldReturnFalse()
    {
        var json = """{"title":"T","categoryId":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","tags":["ok",""]}""";
        ApprovedPublicationMetadata.TryReadPublicProjection(json, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadPublicProjection_WhenEmptyTagsArray_ShouldSucceed()
    {
        var json = ApprovedPublicationMetadata.BuildJson("Title", null, _categoryId, []);
        ApprovedPublicationMetadata.TryReadPublicProjection(json, out ApprovedPublicationMetadata.PublicProjection projection)
            .Should().BeTrue();
        projection.Title.Should().Be("Title");
        projection.Description.Should().BeNull();
        projection.Tags.Should().BeEmpty();
    }

    [Fact]
    public void TryReadPublicProjection_WhenNullDescription_ShouldSucceed()
    {
        var json = ApprovedPublicationMetadata.BuildJson("Title", null, _categoryId, ["alpha"]);
        ApprovedPublicationMetadata.TryReadPublicProjection(json, out ApprovedPublicationMetadata.PublicProjection projection)
            .Should().BeTrue();
        projection.Description.Should().BeNull();
        projection.Tags.Should().Equal("alpha");
    }
}
