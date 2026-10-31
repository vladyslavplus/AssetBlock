using AssetBlock.WebApi.Controllers;
using AssetBlock.WebApi.Filters;
using AwesomeAssertions;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class ModerationSubmissionsControllerTests
{
    [Fact]
    public void Controller_WhenConfigured_ShouldRequirePrivateNoStore()
    {
        Attribute.IsDefined(typeof(ModerationSubmissionsController), typeof(PrivateNoStoreAttribute)).Should().BeTrue();
    }
}
