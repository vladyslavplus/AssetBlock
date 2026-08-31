using Ardalis.Result;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.ProblemDetails;
using AssetBlock.Application.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender => sender;

    protected IActionResult MapResultToActionResult<T>(Result<T> result) =>
        ResultProblemDetailsMapper.Map(HttpContext, result);

    protected IActionResult MapResultToActionResult(Result result) =>
        ResultProblemDetailsMapper.Map(HttpContext, result);

    protected IActionResult UnauthorizedProblem() =>
        ProblemFromCode(StatusCodes.Status401Unauthorized, ErrorCodes.ERR_AUTH_TOKEN_INVALID);

    protected IActionResult ProblemFromCode(int status, string code) =>
        AssetBlockProblemDetails.ToActionResult(AssetBlockProblemDetails.Create(HttpContext, status, code));
}
