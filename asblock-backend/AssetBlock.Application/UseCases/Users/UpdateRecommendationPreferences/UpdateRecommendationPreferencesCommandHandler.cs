using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;

internal sealed class UpdateRecommendationPreferencesCommandHandler(
    IUserStore userStore,
    IRecommendationPersonalizationStore personalizationStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    TimeProvider? timeProvider = null,
    ILogger<UpdateRecommendationPreferencesCommandHandler>? logger = null)
    : IRequestHandler<UpdateRecommendationPreferencesCommand, Result<RecommendationPreferencesDto>>
{
    public async Task<Result<RecommendationPreferencesDto>> Handle(
        UpdateRecommendationPreferencesCommand request,
        CancellationToken cancellationToken)
    {
        User? user = await userStore.GetByIdForUpdate(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        UserRecommendationPreferences? saved = null;

        // Preference write and affinity delete (on opt-out) share one transaction with the audit entry.
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            saved = await personalizationStore.SetPersonalized(request.UserId, request.IsPersonalized, now, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.USER_RECOMMENDATION_PREFERENCES_UPDATE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                request.UserId.ToString(),
                new Dictionary<string, object?> { ["isPersonalized"] = request.IsPersonalized }), ct);
        }, cancellationToken);

        logger?.LogInformation("Recommendation preferences updated: UserId={UserId} IsPersonalized={IsPersonalized}", request.UserId, request.IsPersonalized);
        return Result.Success(new RecommendationPreferencesDto
        {
            IsPersonalized = saved!.IsPersonalized,
            OptedInAt = saved.OptedInAt
        });
    }
}
