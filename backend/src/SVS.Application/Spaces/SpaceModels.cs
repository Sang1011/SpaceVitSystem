namespace SVS.Application.Spaces;

public sealed record SpaceView(Guid Id, string Name, string Role, DateTimeOffset CreatedAt);
public sealed record MemberView(Guid UserId, string DisplayName, string Email, string Role, DateTimeOffset JoinedAt);
public sealed record InvitationView(Guid Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>Mã mời chỉ hiện đúng một lần lúc tạo; DB chỉ giữ băm.</summary>
public sealed record CreatedInvitation(InvitationView Invitation, string Code);
