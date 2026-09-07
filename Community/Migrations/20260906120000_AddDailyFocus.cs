using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260906120000_AddDailyFocus")]
public sealed class AddDailyFocus : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DailyFocusPreferences",
            columns: table => new
            {
                UserId = table.Column<string>(type: "text", nullable: false),
                TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DailyFocusPreferences", x => x.UserId);
                table.ForeignKey("FK_DailyFocusPreferences_AspNetUsers_UserId", x => x.UserId, "AspNetUsers", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateTable(
            name: "DailyFocusSelections",
            columns: table => new
            {
                UserId = table.Column<string>(type: "text", nullable: false),
                Date = table.Column<DateOnly>(type: "date", nullable: false),
                TaskId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DailyFocusSelections", x => new { x.UserId, x.Date, x.TaskId });
                table.ForeignKey("FK_DailyFocusSelections_DailyFocusPreferences_UserId", x => x.UserId, "DailyFocusPreferences", "UserId", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_DailyFocusSelections_TodoTasks_TaskId", x => x.TaskId, "TodoTasks", "Id", onDelete: ReferentialAction.Cascade);
            });
        migrationBuilder.CreateIndex("IX_DailyFocusSelections_TaskId", "DailyFocusSelections", "TaskId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("DailyFocusSelections");
        migrationBuilder.DropTable("DailyFocusPreferences");
    }
}
