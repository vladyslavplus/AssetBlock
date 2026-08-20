using AssetBlock.Application.UseCases.Auth.ConfirmEmailChange;
using AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;
using AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;
using AssetBlock.Application.UseCases.Auth.Login;
using AssetBlock.Application.UseCases.Auth.RefreshToken;
using AssetBlock.Application.UseCases.Auth.Register;
using AssetBlock.Application.UseCases.Auth.RequestPasswordReset;
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
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_REFRESH)]
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

    /// <summary>
    /// Request a password-reset email. Always returns 202 without revealing whether the address exists.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.PASSWORD_RESET_REQUEST)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_PASSWORD_RESET_REQUEST)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestPasswordReset(
        [FromBody] RequestPasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new RequestPasswordResetCommand(request.Email), cancellationToken);
        return result.IsSuccess ? Accepted() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Confirm a password reset using a one-time protected token and new password.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.PASSWORD_RESET_CONFIRM)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM)]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmPasswordReset(
        [FromBody] ConfirmPasswordResetRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(
            new ConfirmPasswordResetCommand(request.Token, request.NewPassword),
            cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Confirm email verification using a one-time protected token.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.EMAIL_VERIFICATION_CONFIRM)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM)]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmEmailVerification(
        [FromBody] ConfirmEmailActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ConfirmEmailVerificationCommand(request.Token), cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Confirm an email-change request using a one-time protected token.
    /// </summary>
    [HttpPost(ApiRoutes.Auth.EMAIL_CHANGE_CONFIRM)]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingConstants.Policies.AUTH_EMAIL_ACTION_CONFIRM)]
    [RequestSizeLimit(16_384)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ConfirmEmailChange(
        [FromBody] ConfirmEmailActionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ConfirmEmailChangeCommand(request.Token), cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }
}
