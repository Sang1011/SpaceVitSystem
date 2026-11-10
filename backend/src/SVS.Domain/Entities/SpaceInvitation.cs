namespace SVS.Domain.Entities;

/// <summary>Lời mời vào Space qua mã dùng một lần. Chỉ lưu băm của mã; người nhận phải đăng nhập bằng đúng email được mời.</summary>
public class SpaceInvitation
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SpaceId { get; set; }
    public Space Space { get; set; } = null!;

    /// <summary>Email được mời, viết thường.</summary>
    public string Email { get; set; } = string.Empty;
    public SpaceRole Role { get; set; } = SpaceRole.Viewer;
    public string CodeHash { get; set; } = string.Empty;

    public Guid? CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }
    public Guid? AcceptedByUserId { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
