using Microsoft.EntityFrameworkCore;
using SVS.Domain.Entities;

namespace SVS.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<User> Users { get; }
    DbSet<Space> Spaces { get; }
    DbSet<SpaceMember> SpaceMembers { get; }
    DbSet<Recording> Recordings { get; }
    DbSet<TranscriptionJob> TranscriptionJobs { get; }
    DbSet<TranscriptSegment> TranscriptSegments { get; }
    DbSet<LiveSession> LiveSessions { get; }
    DbSet<RecordingChunk> RecordingChunks { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
