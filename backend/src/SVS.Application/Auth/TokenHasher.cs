using System.Security.Cryptography;
using System.Text;

namespace SVS.Application.Auth;

/// <summary>Sinh token ngẫu nhiên và băm để lưu. Token gốc chỉ tồn tại ở client; DB chỉ giữ băm.</summary>
public static class TokenHasher
{
    public static string NewToken(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
