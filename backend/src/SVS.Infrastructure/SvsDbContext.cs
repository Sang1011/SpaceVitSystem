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
    }
}
