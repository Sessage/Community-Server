using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260904090000_LimitTodoTaskTitleLength")]
public sealed class LimitTodoTaskTitleLength : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "TodoTasks"
            SET "Title" = LEFT("Title", 500)
            WHERE LENGTH("Title") > 500;
            """);
        migrationBuilder.AlterColumn<string>(
            name: "Title",
            table: "TodoTasks",
            type: "character varying(500)",
            maxLength: 500,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "text");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(
            name: "Title",
            table: "TodoTasks",
            type: "text",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(500)",
            oldMaxLength: 500);
    }
}
