using SVS.Application.Auth;

namespace SVS.Api.Endpoints;

public sealed record RegisterRequest(string? Email, string? Password, string? DisplayName);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record RefreshRequest(string? RefreshToken);
public sealed record UpdateProfileRequest(string? DisplayName);

/// <summary>
/// Đăng ký, đăng nhập, làm mới, đăng xuất, hồ sơ. Các endpoint đăng nhập/làm mới/đăng ký bị giới hạn tần suất theo IP.
/// Client gửi access token qua header "Authorization: Bearer ..."; khi nhận 401 thì gọi refresh một lần rồi thử lại.
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/auth").WithTags("Auth");

        var open = api.MapGroup("").AllowAnonymous().RequireRateLimiting("auth");
        open.MapPost("/register", async (RegisterRequest r, AuthService auth, CancellationToken ct) =>
            Results.Created("/api/auth/me", await auth.RegisterAsync(r.Email, r.Password, r.DisplayName, ct)));
        open.MapPost("/login", async (LoginRequest r, AuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.LoginAsync(r.Email, r.Password, ct)));
        open.MapPost("/refresh", async (RefreshRequest r, AuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.RefreshAsync(r.RefreshToken, ct)));
        // Đăng xuất chỉ cần refresh token (token này tự chứng minh phiên); idempotent nên luôn trả 204.
        open.MapPost("/logout", async (RefreshRequest r, AuthService auth, CancellationToken ct) =>
        {
            await auth.LogoutAsync(r.RefreshToken, ct);
            return Results.NoContent();
        });

        api.MapGet("/me", async (AuthService auth, CancellationToken ct) => Results.Ok(await auth.GetProfileAsync(ct)));
        api.MapPut("/me", async (UpdateProfileRequest r, AuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.UpdateProfileAsync(r.DisplayName, ct)));
        return app;
    }
}
