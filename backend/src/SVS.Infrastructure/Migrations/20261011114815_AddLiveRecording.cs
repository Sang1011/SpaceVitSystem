using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SVS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLiveRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LiveSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Language = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    OutputLanguage = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ChunkSec = table.Column<double>(type: "double precision", nullable: false),
                    StopMode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    TotalChunks = table.Column<int>(type: "integer", nullable: true),
                    ReceivedChunks = table.Column<int>(type: "integer", nullable: false),
                    HighestSeq = table.Column<int>(type: "integer", nullable: false),
                    NextExpectedSeq = table.Column<int>(type: "integer", nullable: false),
                    TotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    AiRegistered = table.Column<bool>(type: "boolean", nullable: false),
                    FinalizeSent = table.Column<bool>(type: "boolean", nullable: false),
                    AiRecreateCount = table.Column<int>(type: "integer", nullable: false),
                    SegmentsSynced = table.Column<int>(type: "integer", nullable: false),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    GapsJson = table.Column<string>(type: "jsonb", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastChunkAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LiveSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LiveSessions_Recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "Recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecordingChunks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LiveSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Seq = table.Column<int>(type: "integer", nullable: false),
                    MediaKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Forwarded = table.Column<bool>(type: "boolean", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordingChunks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordingChunks_LiveSessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "LiveSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_RecordingId",
                table: "LiveSessions",
                column: "RecordingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_Status",
                table: "LiveSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RecordingChunks_LiveSessionId_Seq",
                table: "RecordingChunks",
                columns: new[] { "LiveSessionId", "Seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecordingChunks");

            migrationBuilder.DropTable(
                name: "LiveSessions");
        }
    }
}
