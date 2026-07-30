using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddPortfolioChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DirectInvitationPending",
                table: "ListParticipants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DirectRole",
                table: "ListParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PortfolioRole",
                table: "ListParticipants",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PortfolioLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PortfolioGroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    AddedByUserId = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PortfolioLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PortfolioLists_TodoListGroups_PortfolioGroupId",
                        column: x => x.PortfolioGroupId,
                        principalTable: "TodoListGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PortfolioLists_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioLists_ListId",
                table: "PortfolioLists",
                column: "ListId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PortfolioLists_PortfolioGroupId_SortOrder",
                table: "PortfolioLists",
                columns: new[] { "PortfolioGroupId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PortfolioLists");

            migrationBuilder.DropColumn(
                name: "DirectInvitationPending",
                table: "ListParticipants");

            migrationBuilder.DropColumn(
                name: "DirectRole",
                table: "ListParticipants");

            migrationBuilder.DropColumn(
                name: "PortfolioRole",
                table: "ListParticipants");
        }
    }
}
