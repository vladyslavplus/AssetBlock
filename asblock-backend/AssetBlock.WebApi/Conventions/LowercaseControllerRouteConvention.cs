using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace AssetBlock.WebApi.Conventions;

/// <summary>
/// Makes the [controller] route token lowercase (e.g. api/users instead of api/Users).
/// </summary>
internal sealed class LowercaseControllerRouteConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        controller.ControllerName = controller.ControllerName.ToLowerInvariant();
    }
}
