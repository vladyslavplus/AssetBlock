using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Bundles.ArchiveBundle;

public sealed record ArchiveBundleCommand(Guid BundleId, Guid SellerId) : IRequest<Result>;
