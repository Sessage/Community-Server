using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailImportMessageRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "ListEmailImportedMessages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "AutomationCompletedAtUtc",
                table: "ListEmailImportedMessages",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "ListEmailImportedMessages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TaskCreatedAutomationCompletedAtUtc",
                table: "ListEmailImportedMessages",
                type: "timestamp without time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "ListEmailImportedMessages");

            migrationBuilder.DropColumn(
                name: "AutomationCompletedAtUtc",
                table: "ListEmailImportedMessages");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "ListEmailImportedMessages");

            migrationBuilder.DropColumn(
                name: "TaskCreatedAutomationCompletedAtUtc",
                table: "ListEmailImportedMessages");
        }
    }
}
