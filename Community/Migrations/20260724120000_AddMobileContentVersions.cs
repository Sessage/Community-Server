using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260724120000_AddMobileContentVersions")]
public sealed class AddMobileContentVersions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "ContentVersion", table: "TodoLists", type: "bigint", nullable: false, defaultValue: 1L);
        migrationBuilder.AddColumn<long>(name: "ContentVersion", table: "TodoTasks", type: "bigint", nullable: false, defaultValue: 1L);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ContentVersion", table: "TodoLists");
        migrationBuilder.DropColumn(name: "ContentVersion", table: "TodoTasks");
    }
}
