using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTodoAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoAutomationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    TriggerType = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoAutomationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoAutomationRules_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoAutomationActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CustomFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    LabelId = table.Column<Guid>(type: "uuid", nullable: true),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ConfigurationJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoAutomationActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoAutomationActions_TodoAutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "TodoAutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoAutomationConditions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CustomFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    Value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoAutomationConditions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoAutomationConditions_TodoAutomationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "TodoAutomationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoAutomationActions_RuleId_SortOrder",
                table: "TodoAutomationActions",
                columns: new[] { "RuleId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoAutomationConditions_RuleId_SortOrder",
                table: "TodoAutomationConditions",
                columns: new[] { "RuleId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoAutomationRules_ListId_SortOrder",
                table: "TodoAutomationRules",
                columns: new[] { "ListId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoAutomationActions");

            migrationBuilder.DropTable(
                name: "TodoAutomationConditions");

            migrationBuilder.DropTable(
                name: "TodoAutomationRules");
        }
    }
}
