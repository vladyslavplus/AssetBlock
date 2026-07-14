namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
