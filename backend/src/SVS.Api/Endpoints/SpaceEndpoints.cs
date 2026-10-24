using Microsoft.EntityFrameworkCore;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Api.Endpoints;

public sealed record CreateSpaceRequest(string? Name);
public sealed record SpaceDto(Guid Id, string Name, DateTimeOffset CreatedAt);

/// <summary>Tạo và liệt kê Space ("phòng"). Tối thiểu để thử luồng ghi âm; chưa có chủ sở hữu và thành viên.</summary>
public static class SpaceEndpoints
{
    public static IEndpointRouteBuilder MapSpaceEndpoints(this IEndpointRouteBuilder app)
    {
        // TODO(auth): chưa có đăng nhập nên ai cũng tạo/xem được mọi Space. Khi có auth: người tạo trở thành Owner
        // (SpaceMember) và danh sách chỉ gồm các Space mà người dùng là thành viên.
        var api = app.MapGroup("/api/spaces").WithTags("Spaces");
        api.MapGet("", List);
        api.MapPost("", Create);
        return app;
    }

    private static async Task<IResult> List(IApplicationDbContext db, CancellationToken ct)
    {
        var items = await db.Spaces.AsNoTracking()
            .Where(s => !s.IsArchived).OrderByDescending(s => s.CreatedAt).Take(50)
            .Select(s => new SpaceDto(s.Id, s.Name, s.CreatedAt)).ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> Create(CreateSpaceRequest request, IApplicationDbContext db, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            return Results.BadRequest(new { error = "Tên phòng phải có 1 đến 100 ký tự" });

        var space = new Space { Name = name };
        db.Spaces.Add(space);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/spaces/{space.Id}", new SpaceDto(space.Id, space.Name, space.CreatedAt));
    }
}
