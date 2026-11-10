using Microsoft.EntityFrameworkCore;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Infrastructure;

public class SvsDbContext(DbContextOptions<SvsDbContext> options)
    : DbContext(options), IApplicationDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Space> Spaces => Set<Space>();
    public DbSet<SpaceMember> SpaceMembers => Set<SpaceMember>();
    public DbSet<Recording> Recordings => Set<Recording>();
    public DbSet<TranscriptionJob> TranscriptionJobs => Set<TranscriptionJob>();
    public DbSet<TranscriptSegment> TranscriptSegments => Set<TranscriptSegment>();
    public DbSet<SpaceInvitation> SpaceInvitations => Set<SpaceInvitation>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LiveSession> LiveSessions => Set<LiveSession>();
    public DbSet<RecordingChunk> RecordingChunks => Set<RecordingChunk>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        });

        b.Entity<Space>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        });

        b.Entity<SpaceMember>(e =>
        {
            e.HasKey(x => new { x.SpaceId, x.UserId });
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Space).WithMany(s => s.Members).HasForeignKey(x => x.SpaceId);
            e.HasOne(x => x.User).WithMany(u => u.Memberships).HasForeignKey(x => x.UserId);
        });

        b.Entity<Recording>(e =>
        {
            e.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.MediaKey).HasMaxLength(512);
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
            e.HasOne(x => x.Space).WithMany().HasForeignKey(x => x.SpaceId);
            e.HasIndex(x => x.SpaceId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UploadedByUserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<TranscriptionJob>(e =>
        {
            e.Property(x => x.Mode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.OutputLanguage).HasMaxLength(8).IsRequired();
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.ErrorCode).HasMaxLength(50);
            e.Property(x => x.SummaryError).HasMaxLength(1000);
            e.Property(x => x.SummaryJson).HasColumnType("jsonb");
            e.Property(x => x.StageTimingsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Recording).WithMany(r => r.Jobs).HasForeignKey(x => x.RecordingId);
            e.HasIndex(x => x.RecordingId);
            e.HasIndex(x => x.Status); // poller quét các job đang hoạt động
        });

        b.Entity<TranscriptSegment>(e =>
        {
            e.Property(x => x.Text).IsRequired();
            e.HasOne(x => x.Recording).WithMany(r => r.Segments).HasForeignKey(x => x.RecordingId);
            // Không đặt unique: lúc thay transcript, xóa và thêm cùng (RecordingId, Index) trong một lần SaveChanges.
            e.HasIndex(x => new { x.RecordingId, x.Index });
        });

        b.Entity<LiveSession>(e =>
        {
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.StopMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Language).HasMaxLength(8).IsRequired();
            e.Property(x => x.OutputLanguage).HasMaxLength(8).IsRequired();
            e.Property(x => x.Error).HasMaxLength(1000);
            e.Property(x => x.ErrorCode).HasMaxLength(50);
            e.Property(x => x.GapsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Recording).WithMany().HasForeignKey(x => x.RecordingId);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => x.RecordingId).IsUnique();
            e.HasIndex(x => x.Status); // poller quét các phiên đang hoạt động
        });

        b.Entity<RecordingChunk>(e =>
        {
            e.Property(x => x.MediaKey).HasMaxLength(512).IsRequired();
            e.HasOne(x => x.LiveSession).WithMany(s => s.Chunks).HasForeignKey(x => x.LiveSessionId);
            e.HasIndex(x => new { x.LiveSessionId, x.Seq }).IsUnique(); // chặn nhận trùng seq khi hai request chạy đua
        });

        b.Entity<RefreshToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            // Hai request cùng làm mới một token chạy đua: chỉ một bên ghi được RevokedAt, bên kia gặp xung đột.
            e.Property(x => x.RevokedAt).IsConcurrencyToken();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SpaceInvitation>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.CodeHash).IsUnique();
            e.HasIndex(x => new { x.SpaceId, x.Email });
            e.HasOne(x => x.Space).WithMany().HasForeignKey(x => x.SpaceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
