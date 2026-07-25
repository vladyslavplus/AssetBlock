using Ardalis.Result;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.CreateBundleCheckoutSession;

public sealed record CreateBundleCheckoutSessionCommand(
    Guid BundleId,
    Guid UserId) : IRequest<Result<CreateCheckoutSessionResponse>>;
