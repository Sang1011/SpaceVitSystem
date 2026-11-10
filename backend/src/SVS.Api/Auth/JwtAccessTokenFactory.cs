using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SVS.Application.Auth;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Api.Auth;

/// <summary>Tạo access token JWT ký HS256. Khóa lấy từ cấu hình (Auth:Jwt:Key), được kiểm tra độ dài khi khởi động.</summary>
public sealed class JwtAccessTokenFactory(IOptions<AuthOptions> options) : IAccessTokenFactory
{
    private readonly JsonWebTokenHandler _handler = new();

    public AccessToken Create(User user)
    {
        var jwt = options.Value.Jwt;
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Subject = new ClaimsIdentity(new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            }),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)), SecurityAlgorithms.HmacSha256),
        };
        return new AccessToken(_handler.CreateToken(descriptor), new DateTimeOffset(expires, TimeSpan.Zero));
    }
}
