using SVS.Domain.Entities;

namespace SVS.Application.Common.Interfaces;

public sealed record AccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Tạo access token (JWT) cho người dùng. Cài đặt nằm ở tầng Api cùng với phần xác thực token.</summary>
public interface IAccessTokenFactory
{
    AccessToken Create(User user);
}
