using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SVS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAuthAndMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByUserId",
                table: "Recordings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "LiveSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RefreshTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefreshTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RefreshTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SpaceInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SpaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SpaceInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SpaceInvitations_Spaces_SpaceId",
                        column: x => x.SpaceId,
                        principalTable: "Spaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recordings_UploadedByUserId",
                table: "Recordings",
                column: "UploadedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_LiveSessions_CreatedByUserId",
                table: "LiveSessions",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_FamilyId",
                table: "RefreshTokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_TokenHash",
                table: "RefreshTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RefreshTokens_UserId",
                table: "RefreshTokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SpaceInvitations_CodeHash",
                table: "SpaceInvitations",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SpaceInvitations_SpaceId_Email",
                table: "SpaceInvitations",
                columns: new[] { "SpaceId", "Email" });

            migrationBuilder.AddForeignKey(
                name: "FK_LiveSessions_Users_CreatedByUserId",
                table: "LiveSessions",
                column: "CreatedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Recordings_Users_UploadedByUserId",
                table: "Recordings",
                column: "UploadedByUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LiveSessions_Users_CreatedByUserId",
                table: "LiveSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Recordings_Users_UploadedByUserId",
                table: "Recordings");

            migrationBuilder.DropTable(
                name: "RefreshTokens");

            migrationBuilder.DropTable(
                name: "SpaceInvitations");

            migrationBuilder.DropIndex(
                name: "IX_Recordings_UploadedByUserId",
                table: "Recordings");

            migrationBuilder.DropIndex(
                name: "IX_LiveSessions_CreatedByUserId",
                table: "LiveSessions");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                table: "Recordings");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "LiveSessions");
        }
    }
}
