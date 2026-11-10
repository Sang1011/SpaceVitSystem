using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SVS.Application.Auth;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Application.Spaces;

/// <summary>Space, thành viên và lời mời (FR-SPC-01..04). Mọi thao tác kiểm tra quyền qua SpaceAccessService.</summary>
public sealed class SpaceService(
    IApplicationDbContext db, SpaceAccessService access, ICurrentUser user, IOptions<AuthOptions> options)
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    // ------------------------------------------------------------------ Space

    public async Task<SpaceView> CreateAsync(string? name, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var title = ValidName(name);
        var space = new Space { Name = title };
        db.Spaces.Add(space);
        db.SpaceMembers.Add(new SpaceMember { SpaceId = space.Id, UserId = uid, Role = SpaceRole.Owner });
        await db.SaveChangesAsync(ct);
        return new SpaceView(space.Id, space.Name, SpaceRole.Owner.ToString(), space.CreatedAt);
    }

    /// <summary>Chỉ các Space mà người dùng là thành viên và chưa lưu trữ.</summary>
    public async Task<IReadOnlyList<SpaceView>> ListAsync(CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var rows = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.UserId == uid && !m.Space.IsArchived)
            .OrderByDescending(m => m.Space.CreatedAt).Take(100)
            .Select(m => new { m.SpaceId, m.Space.Name, m.Role, m.Space.CreatedAt }).ToListAsync(ct);
        return rows.Select(r => new SpaceView(r.SpaceId, r.Name, r.Role.ToString(), r.CreatedAt)).ToList();
    }

    public async Task<SpaceView> RenameAsync(Guid spaceId, string? name, CancellationToken ct)
    {
        var role = await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        space.Name = ValidName(name);
        await db.SaveChangesAsync(ct);
        return new SpaceView(space.Id, space.Name, role.ToString(), space.CreatedAt);
    }

    public async Task ArchiveAsync(Guid spaceId, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var space = await db.Spaces.FirstAsync(s => s.Id == spaceId, ct);
        space.IsArchived = true;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ thành viên

    public async Task<IReadOnlyList<MemberView>> ListMembersAsync(Guid spaceId, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Viewer, ct);
        var rows = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId)
            .OrderBy(m => m.JoinedAt)
            .Select(m => new { m.UserId, m.User.DisplayName, m.User.Email, m.Role, m.JoinedAt }).ToListAsync(ct);
        return rows.Select(r => new MemberView(r.UserId, r.DisplayName, r.Email, r.Role.ToString(), r.JoinedAt)).ToList();
    }

    public async Task ChangeRoleAsync(Guid spaceId, Guid memberUserId, SpaceRole newRole, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var member = await db.SpaceMembers.FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == memberUserId, ct)
            ?? throw AppException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy thành viên");
        if (member.Role == SpaceRole.Owner && newRole != SpaceRole.Owner) await EnsureAnotherOwnerAsync(spaceId, ct);
        member.Role = newRole;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Owner xóa bất kỳ ai; thành viên tự rời. Không được để Space không còn Owner.</summary>
    public async Task RemoveMemberAsync(Guid spaceId, Guid memberUserId, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        await access.RequireAsync(spaceId, memberUserId == uid ? SpaceRole.Viewer : SpaceRole.Owner, ct);
        var member = await db.SpaceMembers.FirstOrDefaultAsync(m => m.SpaceId == spaceId && m.UserId == memberUserId, ct)
            ?? throw AppException.NotFound("MEMBER_NOT_FOUND", "Không tìm thấy thành viên");
        if (member.Role == SpaceRole.Owner) await EnsureAnotherOwnerAsync(spaceId, ct);
        db.SpaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ lời mời

    public async Task<CreatedInvitation> InviteAsync(Guid spaceId, string? email, SpaceRole role, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var mail = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (mail.Length == 0 || mail.Length > 256 || !EmailPattern.IsMatch(mail)) throw AppException.Validation("Email không hợp lệ");
        if (role == SpaceRole.Owner) throw AppException.Validation("Chỉ mời với vai trò Editor hoặc Viewer; chuyển quyền Owner bằng cách đổi vai trò thành viên");

        if (await db.SpaceMembers.AnyAsync(m => m.SpaceId == spaceId && m.User.Email == mail, ct))
            throw AppException.Conflict("ALREADY_MEMBER", "Người này đã là thành viên");

        var now = DateTimeOffset.UtcNow;
        var pending = await db.SpaceInvitations
            .Where(i => i.SpaceId == spaceId && i.Email == mail && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct);
        foreach (var old in pending) old.RevokedAt = now; // lời mời mới thay lời mời cũ cùng email

        var code = TokenHasher.NewToken(24);
        var invitation = new SpaceInvitation
        {
            SpaceId = spaceId, Email = mail, Role = role, CodeHash = TokenHasher.Hash(code),
            CreatedByUserId = user.RequireUserId(), CreatedAt = now, ExpiresAt = now.AddDays(options.Value.InvitationDays),
        };
        db.SpaceInvitations.Add(invitation);
        await db.SaveChangesAsync(ct);
        return new CreatedInvitation(ToView(invitation), code);
    }

    public async Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(Guid spaceId, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var now = DateTimeOffset.UtcNow;
        var rows = await db.SpaceInvitations.AsNoTracking()
            .Where(i => i.SpaceId == spaceId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now)
            .OrderByDescending(i => i.CreatedAt).ToListAsync(ct);
        return rows.Select(ToView).ToList();
    }

    public async Task RevokeInvitationAsync(Guid spaceId, Guid invitationId, CancellationToken ct)
    {
        await access.RequireAsync(spaceId, SpaceRole.Owner, ct);
        var inv = await db.SpaceInvitations.FirstOrDefaultAsync(i => i.Id == invitationId && i.SpaceId == spaceId, ct)
            ?? throw AppException.NotFound("INVITATION_NOT_FOUND", "Không tìm thấy lời mời");
        inv.RevokedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Người nhận đăng nhập bằng đúng email được mời rồi nhập mã. Mã dùng một lần.</summary>
    public async Task<SpaceView> AcceptInvitationAsync(string? code, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var invalid = AppException.NotFound("INVITATION_INVALID", "Mã mời không hợp lệ hoặc đã hết hạn");
        if (string.IsNullOrWhiteSpace(code)) throw invalid;

        var hash = TokenHasher.Hash(code.Trim());
        var inv = await db.SpaceInvitations.FirstOrDefaultAsync(i => i.CodeHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (inv is null || inv.AcceptedAt != null || inv.RevokedAt != null || inv.ExpiresAt <= now) throw invalid;

        var me = await db.Users.FirstAsync(u => u.Id == uid, ct);
        if (!string.Equals(me.Email, inv.Email, StringComparison.OrdinalIgnoreCase))
            throw new AppException(403, "INVITATION_EMAIL_MISMATCH", "Lời mời này dành cho một email khác");

        var space = await db.Spaces.FirstAsync(s => s.Id == inv.SpaceId, ct);
        if (space.IsArchived) throw AppException.Conflict("SPACE_ARCHIVED", "Phòng đã được lưu trữ");

        var existing = await db.SpaceMembers.FirstOrDefaultAsync(m => m.SpaceId == inv.SpaceId && m.UserId == uid, ct);
        if (existing is null) db.SpaceMembers.Add(new SpaceMember { SpaceId = inv.SpaceId, UserId = uid, Role = inv.Role });
        inv.AcceptedAt = now;
        inv.AcceptedByUserId = uid;
        await db.SaveChangesAsync(ct);
        return new SpaceView(space.Id, space.Name, (existing?.Role ?? inv.Role).ToString(), space.CreatedAt);
    }

    // ------------------------------------------------------------------ chi tiết

    private async Task EnsureAnotherOwnerAsync(Guid spaceId, CancellationToken ct)
    {
        var owners = await db.SpaceMembers.CountAsync(m => m.SpaceId == spaceId && m.Role == SpaceRole.Owner, ct);
        if (owners <= 1) throw AppException.Conflict("LAST_OWNER", "Phòng phải còn ít nhất một Owner");
    }

    private static string ValidName(string? name)
    {
        var n = name?.Trim() ?? string.Empty;
        if (n.Length is < 1 or > 100) throw AppException.Validation("Tên phòng phải có 1 đến 100 ký tự");
        return n;
    }

    private static InvitationView ToView(SpaceInvitation i) => new(i.Id, i.Email, i.Role.ToString(), i.CreatedAt, i.ExpiresAt);
}
