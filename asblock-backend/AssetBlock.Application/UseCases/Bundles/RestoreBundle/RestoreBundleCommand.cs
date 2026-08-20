using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.RestoreBundle;

public sealed record RestoreBundleCommand(Guid BundleId, Guid SellerId) : IRequest<Result>;
