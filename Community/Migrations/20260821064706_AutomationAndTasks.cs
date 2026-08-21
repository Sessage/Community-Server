using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AutomationAndTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TodoComments_TaskId",
                table: "TodoComments");

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalDecisionAtUtc",
                table: "TodoTasks",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalDecisionByUserId",
                table: "TodoTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ApprovalRequestedAtUtc",
                table: "TodoTasks",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovalRequestedByUserId",
                table: "TodoTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovalStatus",
                table: "TodoTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ApproverUserId",
                table: "TodoTasks",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AuthorUserId",
                table: "TodoComments",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldKey",
                table: "TodoAutomationConditions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FieldKey",
                table: "TodoAutomationActions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IntervalMinutes",
                table: "ListEmailImportConfigurations",
                type: "integer",
                nullable: false,
                defaultValue: 15);

            migrationBuilder.CreateIndex(
                name: "IX_TodoComments_TaskId_AuthorUserId",
                table: "TodoComments",
                columns: new[] { "TaskId", "AuthorUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TodoComments_TaskId_AuthorUserId",
                table: "TodoComments");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionAtUtc",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ApprovalDecisionByUserId",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestedAtUtc",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ApprovalRequestedByUserId",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ApprovalStatus",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "ApproverUserId",
                table: "TodoTasks");

            migrationBuilder.DropColumn(
                name: "AuthorUserId",
                table: "TodoComments");

            migrationBuilder.DropColumn(
                name: "FieldKey",
                table: "TodoAutomationConditions");

            migrationBuilder.DropColumn(
                name: "FieldKey",
                table: "TodoAutomationActions");

            migrationBuilder.DropColumn(
                name: "IntervalMinutes",
                table: "ListEmailImportConfigurations");

            migrationBuilder.CreateIndex(
                name: "IX_TodoComments_TaskId",
                table: "TodoComments",
                column: "TaskId");
        }
    }
}
