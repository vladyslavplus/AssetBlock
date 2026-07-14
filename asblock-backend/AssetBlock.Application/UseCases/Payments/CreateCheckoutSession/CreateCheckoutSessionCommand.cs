using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

public sealed record CreateCheckoutSessionCommand(
    Guid AssetId,
    Guid UserId) : IRequest<Result<CreateCheckoutSessionResponse>>;
