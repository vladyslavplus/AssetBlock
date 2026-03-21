using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace AssetBlock.WebApi.Conventions;

/// <summary>
/// Makes <c>[controller]</c> route token lowercase (e.g. <c>api/users</c> instead of <c>api/Users</c>).
/// </summary>
internal sealed class LowercaseControllerRouteConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        controller.ControllerName = controller.ControllerName.ToLowerInvariant();
    }
}
