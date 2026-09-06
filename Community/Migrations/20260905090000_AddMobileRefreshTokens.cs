using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260905090000_AddMobileRefreshTokens")]
public sealed class AddMobileRefreshTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "MobileRefreshTokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<string>(type: "text", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SecurityStamp = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                ReplacementTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MobileRefreshTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_MobileRefreshTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_MobileRefreshTokens_FamilyId_RevokedAtUtc",
            table: "MobileRefreshTokens",
            columns: new[] { "FamilyId", "RevokedAtUtc" });
        migrationBuilder.CreateIndex(
            name: "IX_MobileRefreshTokens_TokenHash",
            table: "MobileRefreshTokens",
            column: "TokenHash",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_MobileRefreshTokens_UserId_ExpiresAtUtc",
            table: "MobileRefreshTokens",
            columns: new[] { "UserId", "ExpiresAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "MobileRefreshTokens");
}
