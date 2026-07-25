using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Bundles.ArchiveBundle;

public sealed record ArchiveBundleCommand(Guid BundleId, Guid SellerId) : IRequest<Result>;
