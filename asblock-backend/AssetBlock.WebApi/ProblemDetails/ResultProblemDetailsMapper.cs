using Ardalis.Result;
using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.ProblemDetails;

/// <summary>
/// Maps Ardalis.Result failure statuses to RFC 7807 ProblemDetails with AssetBlock extensions.
/// </summary>
internal static class ResultProblemDetailsMapper
{
    public static IActionResult Map(HttpContext httpContext, Result result)
    {
        if (result.IsSuccess)
        {
            return new OkResult();
        }

        return AssetBlockProblemDetails.ToActionResult(
            ToProblemDetails(httpContext, result.Status, result.ValidationErrors, result.Errors));
    }

    public static IActionResult Map<T>(HttpContext httpContext, Result<T> result)
    {
        if (result.IsSuccess)
        {
            return new OkObjectResult(result.Value);
        }

        return AssetBlockProblemDetails.ToActionResult(
            ToProblemDetails(httpContext, result.Status, result.ValidationErrors, result.Errors));
    }

    private static Microsoft.AspNetCore.Mvc.ProblemDetails ToProblemDetails(
        HttpContext httpContext,
        ResultStatus status,
        IEnumerable<ValidationError> validationErrors,
        IEnumerable<string> errors)
    {
        var validationList = validationErrors as IList<ValidationError> ?? validationErrors.ToList();
        var errorList = errors as IList<string> ?? errors.ToList();

        return status switch
        {
            ResultStatus.Invalid => MapInvalid(httpContext, validationList, errorList),
            ResultStatus.NotFound => AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status404NotFound,
                FirstCode(errorList, ErrorCodes.ERR_NOT_FOUND)),
            ResultStatus.Conflict => AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status409Conflict,
                FirstCode(errorList, ErrorCodes.ERR_CONFLICT)),
            ResultStatus.Forbidden => AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status403Forbidden,
                FirstCode(errorList, ErrorCodes.ERR_FORBIDDEN)),
            ResultStatus.Unauthorized => AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status401Unauthorized,
                FirstCode(errorList, ErrorCodes.ERR_AUTH_TOKEN_INVALID)),
            ResultStatus.Error => MapError(httpContext, errorList),
            _ => AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status500InternalServerError,
                FirstCode(errorList, ErrorCodes.ERR_INTERNAL))
        };
    }

    private static Microsoft.AspNetCore.Mvc.ProblemDetails MapError(HttpContext httpContext, IList<string> errorList)
    {
        var code = FirstCode(errorList, ErrorCodes.ERR_INTERNAL);
        if (code == ErrorCodes.ERR_SEARCH_UNAVAILABLE)
        {
            return AssetBlockProblemDetails.Create(
                httpContext,
                StatusCodes.Status503ServiceUnavailable,
                code);
        }

        return AssetBlockProblemDetails.Create(
            httpContext,
            StatusCodes.Status500InternalServerError,
            code);
    }

    private static Microsoft.AspNetCore.Mvc.ProblemDetails MapInvalid(
        HttpContext httpContext,
        IList<ValidationError> validationErrors,
        IList<string> errorList)
    {
        if (validationErrors.Count > 0
            && validationErrors.All(e =>
                !string.IsNullOrWhiteSpace(e.Identifier)
                && !e.Identifier.StartsWith("ERR_", StringComparison.Ordinal)))
        {
            var fieldErrors = validationErrors
                .GroupBy(e => e.Identifier)
                .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage).ToArray());
            return AssetBlockProblemDetails.CreateValidation(httpContext, fieldErrors);
        }

        var code = validationErrors.FirstOrDefault()?.Identifier
            ?? FirstCode(errorList, ErrorCodes.ERR_BAD_REQUEST);
        var detail = validationErrors.FirstOrDefault()?.ErrorMessage
            ?? ErrorCodesToErrorMessages.GetMessage(code);
        return AssetBlockProblemDetails.Create(httpContext, StatusCodes.Status400BadRequest, code, detail);
    }

    private static string FirstCode(IList<string> errors, string fallback)
    {
        var code = errors.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
        return string.IsNullOrWhiteSpace(code) ? fallback : code;
    }
}
