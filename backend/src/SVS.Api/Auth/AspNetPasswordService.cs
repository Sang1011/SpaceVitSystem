using Microsoft.AspNetCore.Identity;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Api.Auth;

/// <summary>Băm mật khẩu bằng PasswordHasher của ASP.NET Core Identity (PBKDF2, có muối, định dạng tự mô tả phiên bản).</summary>
public sealed class AspNetPasswordService : IPasswordService
{
    private static readonly User Anonymous = new();
    private readonly PasswordHasher<User> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(Anonymous, password);

    public bool Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(Anonymous, hash, password) != PasswordVerificationResult.Failed;
}
