using SVS.Application.Common.Exceptions;
using SVS.Application.Spaces;
using SVS.Domain.Entities;

namespace SVS.Api.Endpoints;

public sealed record SpaceNameRequest(string? Name);
public sealed record ChangeRoleRequest(string? Role);
public sealed record InviteRequest(string? Email, string? Role);
public sealed record AcceptInvitationRequest(string? Code);

/// <summary>
/// Space ("phòng"), thành viên và lời mời. Vai trò: Owner (quản lý), Editor (tạo nội dung), Viewer (chỉ xem).
/// Người không phải thành viên nhận 404; thành viên thiếu quyền nhận 403.
/// </summary>
public static class SpaceEndpoints
{
    public static IEndpointRouteBuilder MapSpaceEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/spaces").WithTags("Spaces");
        api.MapGet("", async (SpaceService s, CancellationToken ct) => Results.Ok(await s.ListAsync(ct)));
        api.MapPost("", async (SpaceNameRequest r, SpaceService s, CancellationToken ct) =>
        {
            var space = await s.CreateAsync(r.Name, ct);
            return Results.Created($"/api/spaces/{space.Id}", space);
        });
        api.MapPut("/{spaceId:guid}", async (Guid spaceId, SpaceNameRequest r, SpaceService s, CancellationToken ct) =>
            Results.Ok(await s.RenameAsync(spaceId, r.Name, ct)));
        api.MapPost("/{spaceId:guid}/archive", async (Guid spaceId, SpaceService s, CancellationToken ct) =>
        {
            await s.ArchiveAsync(spaceId, ct);
            return Results.NoContent();
        });

        api.MapGet("/{spaceId:guid}/members", async (Guid spaceId, SpaceService s, CancellationToken ct) =>
            Results.Ok(await s.ListMembersAsync(spaceId, ct)));
        api.MapPut("/{spaceId:guid}/members/{userId:guid}", async (Guid spaceId, Guid userId, ChangeRoleRequest r, SpaceService s, CancellationToken ct) =>
        {
            await s.ChangeRoleAsync(spaceId, userId, ParseRole(r.Role), ct);
            return Results.NoContent();
        });
        api.MapDelete("/{spaceId:guid}/members/{userId:guid}", async (Guid spaceId, Guid userId, SpaceService s, CancellationToken ct) =>
        {
            await s.RemoveMemberAsync(spaceId, userId, ct);
            return Results.NoContent();
        });

        api.MapGet("/{spaceId:guid}/invitations", async (Guid spaceId, SpaceService s, CancellationToken ct) =>
            Results.Ok(await s.ListInvitationsAsync(spaceId, ct)));
        api.MapPost("/{spaceId:guid}/invitations", async (Guid spaceId, InviteRequest r, SpaceService s, CancellationToken ct) =>
            Results.Created($"/api/spaces/{spaceId}/invitations", await s.InviteAsync(spaceId, r.Email, ParseRole(r.Role), ct)));
        api.MapDelete("/{spaceId:guid}/invitations/{invitationId:guid}", async (Guid spaceId, Guid invitationId, SpaceService s, CancellationToken ct) =>
        {
            await s.RevokeInvitationAsync(spaceId, invitationId, ct);
            return Results.NoContent();
        });

        app.MapPost("/api/invitations/accept", async (AcceptInvitationRequest r, SpaceService s, CancellationToken ct) =>
            Results.Ok(await s.AcceptInvitationAsync(r.Code, ct))).WithTags("Spaces");
        return app;
    }

    private static SpaceRole ParseRole(string? role) =>
        Enum.TryParse<SpaceRole>(role, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw AppException.Validation("Vai trò phải là Owner, Editor hoặc Viewer");
}
