using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.ProblemDetails;

internal static class AssetBlockProblemDetails
{
    private const string CONTENT_TYPE = "application/problem+json";
    private const string TYPE_PREFIX = "urn:assetblock:error:";

    public static Microsoft.AspNetCore.Mvc.ProblemDetails Create(
        HttpContext httpContext,
        int status,
        string code,
        string? detail = null,
        string? title = null)
    {
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = TYPE_PREFIX + code,
            Title = title ?? TitleForStatus(status),
            Status = status,
            Detail = detail ?? ErrorCodesToErrorMessages.GetMessage(code),
            Instance = httpContext.Request.Path.HasValue ? httpContext.Request.Path.Value : null
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        return problem;
    }

    public static ValidationProblemDetails CreateValidation(
        HttpContext httpContext,
        IDictionary<string, string[]> errors,
        string? detail = null)
    {
        var problem = new ValidationProblemDetails(errors)
        {
            Type = TYPE_PREFIX + ErrorCodes.ERR_VALIDATION_FAILED,
            Title = "Validation failed",
            Status = StatusCodes.Status400BadRequest,
            Detail = detail ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_VALIDATION_FAILED),
            Instance = httpContext.Request.Path.HasValue ? httpContext.Request.Path.Value : null,
            Extensions = { ["code"] = ErrorCodes.ERR_VALIDATION_FAILED, ["traceId"] = httpContext.TraceIdentifier }
        };
        return problem;
    }

    public static IActionResult ToActionResult(Microsoft.AspNetCore.Mvc.ProblemDetails problem) =>
        new AssetBlockProblemDetailsResult(problem);

    public static ObjectResult ToObjectResult(Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        return new ObjectResult(problem)
        {
            StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError,
            ContentTypes = { CONTENT_TYPE }
        };
    }

    public static async Task WriteAsync(HttpContext httpContext, Microsoft.AspNetCore.Mvc.ProblemDetails problem)
    {
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        httpContext.Response.ContentType = CONTENT_TYPE;
        await httpContext.Response.WriteAsJsonAsync(problem, options: null, contentType: CONTENT_TYPE);
    }

    private static string TitleForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status401Unauthorized => "Unauthorized",
        StatusCodes.Status403Forbidden => "Forbidden",
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status409Conflict => "Conflict",
        StatusCodes.Status429TooManyRequests => "Too Many Requests",
        StatusCodes.Status503ServiceUnavailable => "Service Unavailable",
        _ => "An error occurred"
    };
}
