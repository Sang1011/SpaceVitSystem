namespace SVS.Domain.Entities;

/// <summary>
/// Refresh token xoay vòng. Chỉ lưu băm SHA-256 của token (không lưu token gốc). Các token sinh ra từ một lần đăng nhập
/// cùng một FamilyId: dùng lại token đã bị thay thế được coi là bị đánh cắp và làm vô hiệu cả họ token.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedByTokenId { get; set; }
}
