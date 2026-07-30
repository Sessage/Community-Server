using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnPreferencesAndRemoveOldThings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TodoLists_TodoListGroups_NavigationGroupId",
                table: "TodoLists");

            migrationBuilder.DropIndex(
                name: "IX_TodoLists_NavigationGroupId",
                table: "TodoLists");

            migrationBuilder.DropIndex(
                name: "IX_TodoLists_OwnerId_NavigationGroupId_NavigationSortOrder",
                table: "TodoLists");

            migrationBuilder.DropColumn(
                name: "NavigationGroupId",
                table: "TodoLists");

            migrationBuilder.DropColumn(
                name: "NavigationSortOrder",
                table: "TodoLists");

            migrationBuilder.DropColumn(
                name: "TableColumnOrder",
                table: "TodoLists");

            migrationBuilder.DropColumn(
                name: "TableHiddenColumns",
                table: "TodoLists");

            migrationBuilder.AddColumn<List<string>>(
                name: "TableColumnOrder",
                table: "ListViewPreferences",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "TableHiddenColumns",
                table: "ListViewPreferences",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableColumnOrder",
                table: "ListViewPreferences");

            migrationBuilder.DropColumn(
                name: "TableHiddenColumns",
                table: "ListViewPreferences");

            migrationBuilder.AddColumn<Guid>(
                name: "NavigationGroupId",
                table: "TodoLists",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NavigationSortOrder",
                table: "TodoLists",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<List<string>>(
                name: "TableColumnOrder",
                table: "TodoLists",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.AddColumn<List<string>>(
                name: "TableHiddenColumns",
                table: "TodoLists",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]");

            migrationBuilder.CreateIndex(
                name: "IX_TodoLists_NavigationGroupId",
                table: "TodoLists",
                column: "NavigationGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoLists_OwnerId_NavigationGroupId_NavigationSortOrder",
                table: "TodoLists",
                columns: new[] { "OwnerId", "NavigationGroupId", "NavigationSortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_TodoLists_TodoListGroups_NavigationGroupId",
                table: "TodoLists",
                column: "NavigationGroupId",
                principalTable: "TodoListGroups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
