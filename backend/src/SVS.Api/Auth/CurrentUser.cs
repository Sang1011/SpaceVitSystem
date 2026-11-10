using SVS.Application.Common.Interfaces;

namespace SVS.Api.Auth;

/// <summary>Đọc Id người dùng từ claim "sub" của access token (đã tắt ánh xạ claim mặc định nên tên claim giữ nguyên).</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? UserId =>
        Guid.TryParse(accessor.HttpContext?.User.FindFirst("sub")?.Value, out var id) ? id : null;
}
