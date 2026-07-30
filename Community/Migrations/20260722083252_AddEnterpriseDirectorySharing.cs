using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEnterpriseDirectorySharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectInvitationPending",
                table: "PortfolioParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DirectRole",
                table: "PortfolioParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryRole",
                table: "PortfolioParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryRole",
                table: "ListParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "PortfolioParticipants"
                SET "DirectRole" = "Role", "DirectInvitationPending" = "InvitationPending";
                """);

            migrationBuilder.CreateTable(
                name: "DirectoryIdentities",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    UserPrincipalName = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    GroupIds = table.Column<string[]>(type: "text[]", nullable: false),
                    RefreshedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryIdentities", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_DirectoryIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryShareGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<int>(type: "integer", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalType = table.Column<int>(type: "integer", nullable: false),
                    PrincipalId = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    UserPrincipalName = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryShareGrants", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryIdentities_PrincipalId",
                table: "DirectoryIdentities",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryShareGrants_ResourceType_ResourceId",
                table: "DirectoryShareGrants",
                columns: new[] { "ResourceType", "ResourceId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryShareGrants_ResourceType_ResourceId_PrincipalType_~",
                table: "DirectoryShareGrants",
                columns: new[] { "ResourceType", "ResourceId", "PrincipalType", "PrincipalId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectoryIdentities");

            migrationBuilder.DropTable(
                name: "DirectoryShareGrants");

            migrationBuilder.DropColumn(
                name: "DirectInvitationPending",
                table: "PortfolioParticipants");

            migrationBuilder.DropColumn(
                name: "DirectRole",
                table: "PortfolioParticipants");

            migrationBuilder.DropColumn(
                name: "DirectoryRole",
                table: "PortfolioParticipants");

            migrationBuilder.DropColumn(
                name: "DirectoryRole",
                table: "ListParticipants");
        }
    }
}
