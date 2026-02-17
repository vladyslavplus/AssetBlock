using Ardalis.Result;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.Common;

/// <summary>
/// Builds Result.Invalid from a single error code (message from ErrorCodesToErrorMessages).
/// Use: ResultError.Error&lt;T&gt;(ErrorCodes.ERR_XXX)
/// </summary>
public static class ResultError
{
    public static Result<T> Error<T>(string code) =>
        Result<T>.Invalid(new List<ValidationError> { new(code, ErrorCodesToErrorMessages.GetMessage(code)) });

    public static Result Error(string code) =>
        Result.Invalid(new List<ValidationError> { new(code, ErrorCodesToErrorMessages.GetMessage(code)) });
}
