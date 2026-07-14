using Ardalis.Result;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.Common;

/// <summary>
/// Builds <see cref="Ardalis.Result.Result.Invalid"/> (HTTP 400) from a single error code.
/// Use only for validation-style business failures (bad credentials, business-rule violations,
/// upload/payment failures, etc.). For resource-not-found use <c>Result.NotFound</c>,
/// for duplicate/already-exists use <c>Result.Conflict</c>, and for access-denied use <c>Result.Forbidden</c>.
/// </summary>
public static class ResultError
{
    public static Result<T> Error<T>(string code) =>
        Result<T>.Invalid(new List<ValidationError> { new(code, ErrorCodesToErrorMessages.GetMessage(code)) });

    public static Result Error(string code) =>
        Result.Invalid(new List<ValidationError> { new(code, ErrorCodesToErrorMessages.GetMessage(code)) });
}
