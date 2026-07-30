using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioAndGroupChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPortfolio",
                table: "TodoListGroups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourcePortfolioGroupId",
                table: "ListParticipants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PortfolioGroupId",
                table: "Dashboards",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioInvites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Token = table.Column<string>(type: "text", nullable: false),
                    InviteEmail = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioInvites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioInvites_TodoListGroups_PortfolioGroupId",
                        column: x => x.PortfolioGroupId,
                        principalTable: "TodoListGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PortfolioParticipants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    InvitationPending = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioParticipants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioParticipants_TodoListGroups_PortfolioGroupId",
                        column: x => x.PortfolioGroupId,
                        principalTable: "TodoListGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoListGroupPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsCollapsed = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoListGroupPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoListGroupPreferences_TodoListGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TodoListGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Dashboards_PortfolioGroupId",
                table: "Dashboards",
                column: "PortfolioGroupId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioInvites_PortfolioGroupId",
                table: "PortfolioInvites",
                column: "PortfolioGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioInvites_Token",
                table: "PortfolioInvites",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioParticipants_PortfolioGroupId_Email",
                table: "PortfolioParticipants",
                columns: new[] { "PortfolioGroupId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioParticipants_PortfolioGroupId_UserId",
                table: "PortfolioParticipants",
                columns: new[] { "PortfolioGroupId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoListGroupPreferences_GroupId",
                table: "TodoListGroupPreferences",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoListGroupPreferences_UserId_GroupId",
                table: "TodoListGroupPreferences",
                columns: new[] { "UserId", "GroupId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Dashboards_TodoListGroups_PortfolioGroupId",
                table: "Dashboards",
                column: "PortfolioGroupId",
                principalTable: "TodoListGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Dashboards_TodoListGroups_PortfolioGroupId",
                table: "Dashboards");

            migrationBuilder.DropTable(
                name: "PortfolioInvites");

            migrationBuilder.DropTable(
                name: "PortfolioParticipants");

            migrationBuilder.DropTable(
                name: "TodoListGroupPreferences");

            migrationBuilder.DropIndex(
                name: "IX_Dashboards_PortfolioGroupId",
                table: "Dashboards");

            migrationBuilder.DropColumn(
                name: "IsPortfolio",
                table: "TodoListGroups");

            migrationBuilder.DropColumn(
                name: "SourcePortfolioGroupId",
                table: "ListParticipants");

            migrationBuilder.DropColumn(
                name: "PortfolioGroupId",
                table: "Dashboards");
        }
    }
}
