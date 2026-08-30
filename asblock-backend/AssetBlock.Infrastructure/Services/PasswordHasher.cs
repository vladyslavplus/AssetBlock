using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.Services;

internal sealed class PasswordHasher : IPasswordHasher
{
    private const int WORK_FACTOR = 12;

    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, WORK_FACTOR);

    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);

    public bool NeedsRehash(string hash) => BCrypt.Net.BCrypt.PasswordNeedsRehash(hash, WORK_FACTOR);
}
