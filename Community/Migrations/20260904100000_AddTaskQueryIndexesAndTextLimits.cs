using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260904100000_AddTaskQueryIndexesAndTextLimits")]
public sealed class AddTaskQueryIndexesAndTextLimits : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "TodoComments" SET "Message" = LEFT("Message", 4000) WHERE LENGTH("Message") > 4000;
            UPDATE "TodoSteps" SET "Title" = LEFT("Title", 500) WHERE LENGTH("Title") > 500;
            """);
        migrationBuilder.AlterColumn<string>(name: "Message", table: "TodoComments", type: "character varying(4000)", maxLength: 4000, nullable: false, oldClrType: typeof(string), oldType: "text");
        migrationBuilder.AlterColumn<string>(name: "Title", table: "TodoSteps", type: "character varying(500)", maxLength: 500, nullable: false, oldClrType: typeof(string), oldType: "text");

        migrationBuilder.DropIndex(name: "IX_TodoTasks_ListId", table: "TodoTasks");
        migrationBuilder.CreateIndex(name: "IX_TodoTasks_DueDate_DeletedAt_Done", table: "TodoTasks", columns: new[] { "DueDate", "DeletedAt", "Done" });
        migrationBuilder.CreateIndex(name: "IX_TodoTasks_ListId_DeletedAt_Column_KanbanSortOrder", table: "TodoTasks", columns: new[] { "ListId", "DeletedAt", "Column", "KanbanSortOrder" });
        migrationBuilder.CreateIndex(name: "IX_TodoTasks_ListId_DeletedAt_Done_ListSortOrder", table: "TodoTasks", columns: new[] { "ListId", "DeletedAt", "Done", "ListSortOrder" });
        migrationBuilder.CreateIndex(name: "IX_TodoTasks_ReminderAtUtc_ReminderSentAtUtc_Done_DeletedAt", table: "TodoTasks", columns: new[] { "ReminderAtUtc", "ReminderSentAtUtc", "Done", "DeletedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_TodoTasks_DueDate_DeletedAt_Done", table: "TodoTasks");
        migrationBuilder.DropIndex(name: "IX_TodoTasks_ListId_DeletedAt_Column_KanbanSortOrder", table: "TodoTasks");
        migrationBuilder.DropIndex(name: "IX_TodoTasks_ListId_DeletedAt_Done_ListSortOrder", table: "TodoTasks");
        migrationBuilder.DropIndex(name: "IX_TodoTasks_ReminderAtUtc_ReminderSentAtUtc_Done_DeletedAt", table: "TodoTasks");
        migrationBuilder.CreateIndex(name: "IX_TodoTasks_ListId", table: "TodoTasks", column: "ListId");
        migrationBuilder.AlterColumn<string>(name: "Message", table: "TodoComments", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(4000)", oldMaxLength: 4000);
        migrationBuilder.AlterColumn<string>(name: "Title", table: "TodoSteps", type: "text", nullable: false, oldClrType: typeof(string), oldType: "character varying(500)", oldMaxLength: 500);
    }
}
