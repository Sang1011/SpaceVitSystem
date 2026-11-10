namespace SVS.Application.Auth;

/// <summary>Mục "Auth" trong cấu hình. Auth:Jwt:Key là bí mật: đặt bằng user-secrets hoặc biến môi trường, không commit.</summary>
public sealed class AuthOptions
{
    public JwtOptions Jwt { get; set; } = new();
    public int RefreshTokenDays { get; set; } = 30;
    public int InvitationDays { get; set; } = 7;
    /// <summary>Dùng lại refresh token vừa bị thay thế trong khoảng này bị coi là hai tab chạy đua, không phải trộm token.</summary>
    public int RefreshGraceSeconds { get; set; } = 10;
    public int MinPasswordLength { get; set; } = 8;
}

public sealed class JwtOptions
{
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "svs-api";
    public string Audience { get; set; } = "svs-clients";
    public int AccessTokenMinutes { get; set; } = 15;
}
