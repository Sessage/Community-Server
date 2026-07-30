using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTableColumnOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<List<string>>(
                name: "TableColumnOrder",
                table: "TodoLists",
                type: "text[]",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "TodoLists"
                SET "TableColumnOrder" = ARRAY[]::text[]
                WHERE "TableColumnOrder" IS NULL;
                """);

            migrationBuilder.AlterColumn<List<string>>(
                name: "TableColumnOrder",
                table: "TodoLists",
                type: "text[]",
                nullable: false,
                defaultValueSql: "ARRAY[]::text[]",
                oldClrType: typeof(List<string>),
                oldType: "text[]",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TableColumnOrder",
                table: "TodoLists");
        }
    }
}
