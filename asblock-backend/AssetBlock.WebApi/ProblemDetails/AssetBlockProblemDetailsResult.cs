using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.ProblemDetails;

/// <summary>
/// Writes ProblemDetails with Content-Type application/problem+json (MVC formatters would use application/json).
/// </summary>
internal sealed class AssetBlockProblemDetailsResult(Microsoft.AspNetCore.Mvc.ProblemDetails problem) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        await AssetBlockProblemDetails.WriteAsync(context.HttpContext, problem);
    }
}
