using Microsoft.EntityFrameworkCore;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Application.Spaces;

/// <summary>
/// Kiểm tra quyền theo Space (FR-SPC-03). Quy ước: người không phải thành viên nhận 404 (không lộ Space có tồn tại);
/// thành viên thiếu quyền nhận 403. Owner > Editor > Viewer. Space đã lưu trữ chỉ cho đọc.
/// Các endpoint gọi lớp này TRƯỚC khi gọi coordinator; coordinator và tác vụ nền không biết gì về người dùng.
/// </summary>
public sealed class SpaceAccessService(IApplicationDbContext db, ICurrentUser user)
{
    public async Task<SpaceRole> RequireAsync(Guid spaceId, SpaceRole minimum, CancellationToken ct)
    {
        var uid = user.RequireUserId();
        var row = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == spaceId && m.UserId == uid)
            .Select(m => new { m.Role, m.Space.IsArchived })
            .FirstOrDefaultAsync(ct);
        if (row is null) throw AppException.NotFound("SPACE_NOT_FOUND", "Không tìm thấy phòng");
        if (!row.Role.AtLeast(minimum)) throw AppException.Forbidden();
        if (minimum != SpaceRole.Viewer && row.IsArchived)
            throw AppException.Conflict("SPACE_ARCHIVED", "Phòng đã được lưu trữ, chỉ có thể xem");
        return row.Role;
    }

    public async Task<Guid> RequireRecordingAsync(Guid recordingId, SpaceRole minimum, CancellationToken ct)
    {
        var spaceId = await db.Recordings.AsNoTracking().Where(r => r.Id == recordingId)
            .Select(r => (Guid?)r.SpaceId).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("RECORDING_NOT_FOUND", "Không tìm thấy bản ghi");
        await RequireAsync(spaceId, minimum, ct); // không phải thành viên thì vẫn là 404
        return spaceId;
    }

    public async Task<Guid> RequireJobAsync(Guid jobId, SpaceRole minimum, CancellationToken ct)
    {
        var spaceId = await db.TranscriptionJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Select(j => (Guid?)j.Recording.SpaceId).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("JOB_NOT_FOUND", "Không tìm thấy job");
        await RequireAsync(spaceId, minimum, ct);
        return spaceId;
    }

    /// <summary>
    /// Phiên live. Với thao tác ghi (creatorOnly): chỉ chủ phiên hoặc Owner của Space. Phiên cũ không có chủ (dữ liệu tạo
    /// trước khi có đăng nhập) cho mọi Editor trở lên.
    /// </summary>
    public async Task<Guid> RequireLiveSessionAsync(Guid sessionId, SpaceRole minimum, bool creatorOnly, CancellationToken ct)
    {
        var row = await db.LiveSessions.AsNoTracking().Where(s => s.Id == sessionId)
            .Select(s => new { s.Recording.SpaceId, s.CreatedByUserId }).FirstOrDefaultAsync(ct)
            ?? throw AppException.NotFound("SESSION_NOT_FOUND", "Không tìm thấy phiên ghi âm");
        var role = await RequireAsync(row.SpaceId, minimum, ct);
        if (creatorOnly && row.CreatedByUserId is { } owner && owner != user.RequireUserId() && role != SpaceRole.Owner)
            throw AppException.Forbidden("Chỉ người bắt đầu phiên hoặc chủ phòng được thao tác");
        return row.SpaceId;
    }
}
