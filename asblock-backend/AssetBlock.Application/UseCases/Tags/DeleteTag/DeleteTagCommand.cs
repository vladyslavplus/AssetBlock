using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Tags.DeleteTag;

public sealed record DeleteTagCommand(Guid Id) : IRequest<Result>;
