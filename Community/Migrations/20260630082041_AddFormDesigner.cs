using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddFormDesigner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TodoForms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ListId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Slug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PublicationStatus = table.Column<int>(type: "integer", nullable: false),
                    PasswordSalt = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoForms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoForms_TodoLists_ListId",
                        column: x => x.ListId,
                        principalTable: "TodoLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoFormFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    StandardField = table.Column<int>(type: "integer", nullable: true),
                    CustomFieldId = table.Column<Guid>(type: "uuid", nullable: true),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoFormFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoFormFields_TodoForms_FormId",
                        column: x => x.FormId,
                        principalTable: "TodoForms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TodoFormSubmissionKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IpHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TodoFormSubmissionKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TodoFormSubmissionKeys_TodoForms_FormId",
                        column: x => x.FormId,
                        principalTable: "TodoForms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TodoFormFields_FormId_SortOrder",
                table: "TodoFormFields",
                columns: new[] { "FormId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoForms_ListId_Name",
                table: "TodoForms",
                columns: new[] { "ListId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoForms_Slug",
                table: "TodoForms",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TodoFormSubmissionKeys_FormId_IpHash_CreatedAtUtc",
                table: "TodoFormSubmissionKeys",
                columns: new[] { "FormId", "IpHash", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TodoFormSubmissionKeys_FormId_SubmissionKey",
                table: "TodoFormSubmissionKeys",
                columns: new[] { "FormId", "SubmissionKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TodoFormFields");

            migrationBuilder.DropTable(
                name: "TodoFormSubmissionKeys");

            migrationBuilder.DropTable(
                name: "TodoForms");
        }
    }
}
