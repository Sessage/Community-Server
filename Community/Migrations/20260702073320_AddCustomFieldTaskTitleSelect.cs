using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomFieldTaskTitleSelect : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceTaskListId",
                table: "TodoCustomFields",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodoCustomFields_SourceTaskListId",
                table: "TodoCustomFields",
                column: "SourceTaskListId");

            migrationBuilder.AddForeignKey(
                name: "FK_TodoCustomFields_TodoLists_SourceTaskListId",
                table: "TodoCustomFields",
                column: "SourceTaskListId",
                principalTable: "TodoLists",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_TodoCustomFields_TodoLists_SourceTaskListId",
                table: "TodoCustomFields");

            migrationBuilder.DropIndex(
                name: "IX_TodoCustomFields_SourceTaskListId",
                table: "TodoCustomFields");

            migrationBuilder.DropColumn(
                name: "SourceTaskListId",
                table: "TodoCustomFields");
        }
    }
}
