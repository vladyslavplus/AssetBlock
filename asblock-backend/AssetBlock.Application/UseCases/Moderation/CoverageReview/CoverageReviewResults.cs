using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using FluentValidation;
namespace AssetBlock.Application.UseCases.Moderation.CoverageReview;

internal static class CoverageReviewResults
{
    public static Result<T> Map<T>(ReviewAccessResult<T> result) => result.Status switch
    {
        ReviewAccessStatus.SUCCESS => Result.Success(result.Value!),
        ReviewAccessStatus.FORBIDDEN => Result.Forbidden(ErrorCodes.ERR_FORBIDDEN),
        ReviewAccessStatus.NOT_FOUND => Result.NotFound(ErrorCodes.ERR_REVIEW_EVIDENCE_NOT_FOUND),
        ReviewAccessStatus.STALE or ReviewAccessStatus.CONFLICT => Result.Conflict(result.ReasonCode ?? ErrorCodes.ERR_COVERAGE_REVIEW_CONFLICT),
        ReviewAccessStatus.SECURITY_RESTRICTED => Result.Forbidden(ErrorCodes.ERR_EVIDENCE_SECURITY_RESTRICTED),
        ReviewAccessStatus.AUDIT_UNAVAILABLE => Result.Unavailable(ErrorCodes.ERR_EVIDENCE_AUDIT_UNAVAILABLE),
        ReviewAccessStatus.SCOPE_UNAVAILABLE => Result.Conflict(ErrorCodes.ERR_COVERAGE_SCOPE_UNAVAILABLE),
        _ => ResultError.Error<T>(ErrorCodes.ERR_VALIDATION_FAILED)
    };
}
