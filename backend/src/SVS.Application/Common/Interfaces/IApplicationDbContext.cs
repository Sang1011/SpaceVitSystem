using Microsoft.EntityFrameworkCore;
using SVS.Domain.Entities;

namespace SVS.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Space> Spaces { get; }
    DbSet<SpaceMember> SpaceMembers { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}