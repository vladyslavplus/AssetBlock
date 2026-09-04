using System.Reflection;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Controllers;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Tests.Controllers;

public sealed class SellerBundlesControllerTests
{
    [Fact]
    public void Controller_ShouldUseApiRoutesSellerBundlesBaseRoute()
    {
        RouteAttribute? routeAttr = typeof(SellerBundlesController).GetCustomAttribute<RouteAttribute>(inherit: false);
        routeAttr.Should().NotBeNull();
        routeAttr.Template.Should().Be(ApiRoutes.SellerBundles.BASE);
        ApiRoutes.SellerBundles.BASE.Should().Be("api/seller/bundles");
    }

    [Fact]
    public void Controller_ShouldInheritApiControllerBase()
    {
        typeof(SellerBundlesController).IsSubclassOf(typeof(ApiControllerBase)).Should().BeTrue();
    }

    [Fact]
    public void Controller_ShouldRequireAuthorization()
    {
        AuthorizeAttribute? authAttr = typeof(SellerBundlesController).GetCustomAttribute<AuthorizeAttribute>();
        authAttr.Should().NotBeNull();
    }
}
