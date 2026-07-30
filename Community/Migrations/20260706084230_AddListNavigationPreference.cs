using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddListNavigationPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoListNavigationPreferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    NavigationGroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    NavigationSortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoListNavigationPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoListNavigationPreferences_TodoListGroups_NavigationGrou~",
                        column: x => x.NavigationGroupId,
                        principalTable: "TodoListGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TodoListNavigationPreferences_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoListNavigationPreferences_ListId",
                table: "TodoListNavigationPreferences",
                column: "ListId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoListNavigationPreferences_NavigationGroupId",
                table: "TodoListNavigationPreferences",
                column: "NavigationGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoListNavigationPreferences_UserId_ListId",
                table: "TodoListNavigationPreferences",
                columns: new[] { "UserId", "ListId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodoListNavigationPreferences_UserId_NavigationGroupId_Navi~",
                table: "TodoListNavigationPreferences",
                columns: new[] { "UserId", "NavigationGroupId", "NavigationSortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoListNavigationPreferences");
        }
    }
}
