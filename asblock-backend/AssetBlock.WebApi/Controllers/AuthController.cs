using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Application.UseCases.Auth.RefreshToken;
using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

public sealed class AuthController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Login with email and password. Returns access and refresh tokens.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.LOGIN)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_LOGIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        var command = new LoginCommand(request.Email, request.Password);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Exchange a valid refresh token for a new access and refresh token pair.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.REFRESH)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        var command = new RefreshTokenCommand(request.RefreshToken);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Register a new user with email and password.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.REGISTER)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_REGISTER)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken cancellationToken)
    {
        var command = new RegisterCommand(request.Username, request.Email, request.Password);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }
}
