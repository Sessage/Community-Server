using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "DirectoryIdentities",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "DirectoryIdentities",
                type: "timestamp without time zone",
                nullable: true);

            // Vor dieser Migration entstanden DirectoryIdentities ausschließlich nach einer
            // erfolgreichen LDAP-/AD-Anmeldung und sind daher bereits verifiziert.
            migrationBuilder.Sql("""
                UPDATE "DirectoryIdentities"
                SET "VerifiedAtUtc" = "RefreshedAtUtc",
                    "Email" = CASE
                        WHEN "UserPrincipalName" LIKE '%@%' THEN "UserPrincipalName"
                        ELSE ''
                    END;
                """);

            migrationBuilder.CreateTable(
                name: "DirectoryInvitationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ResourceType = table.Column<int>(type: "integer", nullable: false),
                    ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SentAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryInvitationDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectoryInvitationDeliveries_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryInvitationDeliveries_ResourceType_ResourceId_UserId",
                table: "DirectoryInvitationDeliveries",
                columns: new[] { "ResourceType", "ResourceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryInvitationDeliveries_UserId",
                table: "DirectoryInvitationDeliveries",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectoryInvitationDeliveries");

            migrationBuilder.DropColumn(
                name: "Email",
                table: "DirectoryIdentities");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "DirectoryIdentities");
        }
    }
}
