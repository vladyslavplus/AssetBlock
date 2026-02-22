using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Tags.DeleteTag;

public sealed record DeleteTagCommand(Guid Id) : IRequest<Result>;
