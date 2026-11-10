namespace SVS.Application.Auth;

public sealed record UserProfile(Guid Id, string Email, string DisplayName, DateTimeOffset CreatedAt);

public sealed record AuthResult(
    string AccessToken, DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken, DateTimeOffset RefreshTokenExpiresAt, UserProfile User);
