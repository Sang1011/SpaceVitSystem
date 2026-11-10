using SVS.Application.Common.Exceptions;

namespace SVS.Application.Common.Interfaces;

/// <summary>Người dùng của request hiện tại (lấy từ access token). Tầng Application không biết gì về HTTP.</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}

public static class CurrentUserExtensions
{
    public static Guid RequireUserId(this ICurrentUser user) =>
        user.UserId ?? throw AppException.Unauthorized("UNAUTHENTICATED", "Cần đăng nhập");
}
