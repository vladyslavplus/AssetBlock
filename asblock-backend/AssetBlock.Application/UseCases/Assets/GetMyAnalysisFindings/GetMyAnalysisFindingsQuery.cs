using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetMyAnalysisFindings;

public sealed record GetMyAnalysisFindingsQuery(Guid AssetVersionId, Guid ReportId, Guid OwnerId, int Page)
    : IRequest<Result<SellerAnalysisFindingsPageDto>>;
