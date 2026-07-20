namespace AssetBlock.Domain.Core.Dto.Auth;

public sealed record ConfirmEmailActionRequest(string Token);

public sealed record RequestPasswordResetRequest(string Email);

public sealed record ConfirmPasswordResetRequest(string Token, string NewPassword);

public sealed record RequestEmailChangeRequest(string NewEmail, string CurrentPassword);

