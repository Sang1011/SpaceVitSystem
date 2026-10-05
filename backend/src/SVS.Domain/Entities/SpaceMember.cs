namespace SVS.Domain.Entities;

public class SpaceMember
{
    public Guid SpaceId { get; set; }
    public Space Space { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public SpaceRole Role { get; set; } = SpaceRole.Viewer;
    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;
}