using System.Security.Claims;
using Ardalis.Result;
using AssetBlock.Domain.Core.Constants;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender => sender;

    protected Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    protected IActionResult MapResultToActionResult<T>(Result<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Status switch
        {
            ResultStatus.Invalid => BadRequest(new { errors = result.ValidationErrors.Select(e => new { identifier = e.Identifier, message = e.ErrorMessage }).ToList() }),
            ResultStatus.NotFound => NotFound(ErrorsBody(result.Errors.FirstOrDefault() ?? ErrorCodes.ERR_NOT_FOUND)),
            ResultStatus.Conflict => StatusCode(409, ErrorsBody(result.Errors.FirstOrDefault() ?? ErrorCodes.ERR_CONFLICT)),
            ResultStatus.Forbidden => StatusCode(403, ErrorsBody(ErrorCodes.ERR_FORBIDDEN, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_FORBIDDEN))),
            ResultStatus.Unauthorized => Unauthorized(ErrorsBody(ErrorCodes.ERR_AUTH_TOKEN_INVALID, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AUTH_TOKEN_INVALID))),
            _ => BadRequest(ErrorsBody(ErrorCodes.ERR_BAD_REQUEST, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BAD_REQUEST)))
        };
    }

    protected IActionResult MapResultToActionResult(Result result)
    {
        if (result.IsSuccess)
        {
            return Ok();
        }

        return result.Status switch
        {
            ResultStatus.Invalid => BadRequest(new { errors = result.ValidationErrors.Select(e => new { identifier = e.Identifier, message = e.ErrorMessage }).ToList() }),
            ResultStatus.NotFound => NotFound(ErrorsBody(result.Errors.FirstOrDefault() ?? ErrorCodes.ERR_NOT_FOUND)),
            ResultStatus.Conflict => StatusCode(409, ErrorsBody(result.Errors.FirstOrDefault() ?? ErrorCodes.ERR_CONFLICT)),
            ResultStatus.Forbidden => StatusCode(403, ErrorsBody(ErrorCodes.ERR_FORBIDDEN, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_FORBIDDEN))),
            ResultStatus.Unauthorized => Unauthorized(ErrorsBody(ErrorCodes.ERR_AUTH_TOKEN_INVALID, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_AUTH_TOKEN_INVALID))),
            _ => BadRequest(ErrorsBody(ErrorCodes.ERR_BAD_REQUEST, result.Errors.FirstOrDefault() ?? ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BAD_REQUEST)))
        };
    }

    private static object ErrorsBody(string identifier) =>
        new { errors = new[] { new { identifier, message = ErrorCodesToErrorMessages.GetMessage(identifier) } } };

    private static object ErrorsBody(string identifier, string message) =>
        new { errors = new[] { new { identifier, message } } };
}
