using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoCustomFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoCustomFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoCustomFields_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoCustomFieldOptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "character varying(240)", maxLength: 240, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoCustomFieldOptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoCustomFieldOptions_TodoCustomFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "TodoCustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoTaskCustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoTaskCustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoTaskCustomFieldValues_TodoCustomFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "TodoCustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TodoTaskCustomFieldValues_TodoTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "TodoTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoCustomFieldOptions_FieldId_SortOrder",
                table: "TodoCustomFieldOptions",
                columns: new[] { "FieldId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoCustomFields_ListId_SortOrder",
                table: "TodoCustomFields",
                columns: new[] { "ListId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoTaskCustomFieldValues_FieldId",
                table: "TodoTaskCustomFieldValues",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_TodoTaskCustomFieldValues_TaskId_FieldId",
                table: "TodoTaskCustomFieldValues",
                columns: new[] { "TaskId", "FieldId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoCustomFieldOptions");

            migrationBuilder.DropTable(
                name: "TodoTaskCustomFieldValues");

            migrationBuilder.DropTable(
                name: "TodoCustomFields");
        }
    }
}
