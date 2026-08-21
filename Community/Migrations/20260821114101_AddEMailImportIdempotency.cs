using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddEMailImportIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListEmailImportedMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConfigurationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolderName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    MessageUid = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListEmailImportedMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListEmailImportedMessages_ListEmailImportConfigurations_Con~",
                        column: x => x.ConfigurationId,
                        principalTable: "ListEmailImportConfigurations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListEmailImportedMessages_ConfigurationId_FolderName_UidVal~",
                table: "ListEmailImportedMessages",
                columns: new[] { "ConfigurationId", "FolderName", "UidValidity", "MessageUid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListEmailImportedMessages_TaskId",
                table: "ListEmailImportedMessages",
                column: "TaskId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListEmailImportedMessages");
        }
    }
}
