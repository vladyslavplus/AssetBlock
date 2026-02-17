using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

public sealed record CreateCheckoutSessionCommand(
    Guid AssetId,
    Guid UserId,
    string SuccessUrl,
    string CancelUrl) : IRequest<Result<CreateCheckoutSessionResponse>>;
