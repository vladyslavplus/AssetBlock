using System.Reflection;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class SellerCollectionsControllerTests
{
    [Fact]
    public void Controller_ShouldUseApiRoutesSellerCollectionsBaseRoute()
    {
        RouteAttribute? routeAttr = typeof(SellerCollectionsController).GetCustomAttribute<RouteAttribute>(inherit: false);
        routeAttr.Should().NotBeNull();
        routeAttr.Template.Should().Be(ApiRoutes.SellerCollections.BASE);
        ApiRoutes.SellerCollections.BASE.Should().Be("api/seller/collections");
    }

    [Fact]
    public void Controller_ShouldInheritApiControllerBase()
    {
        typeof(SellerCollectionsController).IsSubclassOf(typeof(ApiControllerBase)).Should().BeTrue();
    }
}
