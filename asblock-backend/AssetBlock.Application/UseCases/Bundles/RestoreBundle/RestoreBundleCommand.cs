using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.RestoreBundle;

public sealed record RestoreBundleCommand(Guid BundleId, Guid SellerId) : IRequest<Result>;
