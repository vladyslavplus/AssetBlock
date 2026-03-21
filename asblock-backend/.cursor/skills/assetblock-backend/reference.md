# AssetBlock backend — reference snippets

Illustrative patterns aligned with the codebase. Adjust namespaces and types to match the feature.

## MediatR handler (application)

```csharp
internal sealed class ExampleCommandHandler(
    IExampleStore exampleStore,
    ILogger<ExampleCommandHandler> logger) : IRequestHandler<ExampleCommand, Result<ExampleDto>>
{
    public async Task<Result<ExampleDto>> Handle(ExampleCommand request, CancellationToken cancellationToken)
    {
        var entity = await exampleStore.GetById(request.Id, cancellationToken);
        if (entity is null)
        {
            return Result.NotFound(ErrorCodes.ERR_NOT_FOUND);
        }

        if (entity.OwnerId != request.CurrentUserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        return Result.Success(new ExampleDto(entity.Id, entity.Name));
    }
}
```

`ResultError.Error<T>(ErrorCodes.ERR_XXX)` is used when the failure should carry a message from `ErrorCodesToErrorMessages` (maps to `ResultStatus.Invalid` and `ValidationErrors` in Ardalis).

## Web API controller

```csharp
public sealed class ExampleController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>Short description for OpenAPI.</summary>
    [HttpGet(ApiRoutes.Example.BY_ID)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ExampleQuery(id), cancellationToken);
        return MapResultToActionResult(result);
    }

    [HttpPost(ApiRoutes.Example.CREATE)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.EXAMPLE)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateExampleRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new CreateExampleCommand(userId.Value, request.Name), cancellationToken);
        return MapResultToActionResult(result);
    }
}
```

- Route segments live in `ApiRoutes`; do not inline string routes.
- Use `MapResultToActionResult` for `Result<T>` / `Result` from handlers.

## Further reading

- Full conventions: [SKILL.md](SKILL.md)
- Unit tests: [../assetblock-backend-testing/SKILL.md](../assetblock-backend-testing/SKILL.md)
