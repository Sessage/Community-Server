using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddReminderDeliveryAndDashboardRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderEmailSentAtUtc",
                table: "TodoTasks",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReminderInboxSentAtUtc",
                table: "TodoTasks",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ContentVersion",
                table: "Dashboards",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReminderEmailSentAtUtc",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ReminderInboxSentAtUtc",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ContentVersion",
                table: "Dashboards");
        }
    }
}
