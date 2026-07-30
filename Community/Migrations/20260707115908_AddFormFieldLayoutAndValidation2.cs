using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFormFieldLayoutAndValidation2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                table: "TodoFormSubmissionKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackgroundColor",
                table: "TodoForms",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ButtonColor",
                table: "TodoForms",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CapacityReachedText",
                table: "TodoForms",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxSubmissions",
                table: "TodoForms",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodoFormSubmissionKeys_FormId_TaskId",
                table: "TodoFormSubmissionKeys",
                columns: new[] { "FormId", "TaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TodoFormSubmissionKeys_FormId_TaskId",
                table: "TodoFormSubmissionKeys");

            migrationBuilder.DropColumn(
                name: "TaskId",
                table: "TodoFormSubmissionKeys");

            migrationBuilder.DropColumn(
                name: "BackgroundColor",
                table: "TodoForms");

            migrationBuilder.DropColumn(
                name: "ButtonColor",
                table: "TodoForms");

            migrationBuilder.DropColumn(
                name: "CapacityReachedText",
                table: "TodoForms");

            migrationBuilder.DropColumn(
                name: "MaxSubmissions",
                table: "TodoForms");
        }
    }
}
