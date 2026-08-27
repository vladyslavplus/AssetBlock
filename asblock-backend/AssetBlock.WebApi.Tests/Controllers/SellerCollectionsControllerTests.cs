using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class SellerCollectionsControllerTests
{
    [Fact]
    public void Controller_ShouldUseApiRoutesSellerCollectionsBaseRoute()
    {
        var routeAttr = typeof(SellerCollectionsController).GetCustomAttribute<RouteAttribute>();
        routeAttr.Should().NotBeNull();
        routeAttr.Template.Should().Be(ApiRoutes.SellerCollections.BASE);
        ApiRoutes.SellerCollections.BASE.Should().Be("api/seller/collections");
    }
}
