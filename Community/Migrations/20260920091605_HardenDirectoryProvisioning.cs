using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class HardenDirectoryProvisioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Alte Versionen speicherten DNs mit der vom LDAP-Server gelieferten
            // Gross-/Kleinschreibung. Der aktuelle Abgleich verwendet eine kanonische
            // Darstellung; deshalb muessen Bestandsdaten vor dem eindeutigen Index
            // ebenfalls normalisiert werden.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "DirectoryIdentities"
                        GROUP BY UPPER(BTRIM("PrincipalId"))
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION USING
                            MESSAGE = 'DirectoryIdentities enthaelt mehrfach zugeordnete Verzeichnisidentitaeten.',
                            HINT = 'Bereinigen Sie doppelte PrincipalId-Zuordnungen vor dem Update; eine Verzeichnisidentitaet darf nur einem lokalen Konto gehoeren.';
                    END IF;
                END $$;

                UPDATE "DirectoryIdentities"
                SET "PrincipalId" = UPPER(BTRIM("PrincipalId")),
                    "GroupIds" = ARRAY(
                        SELECT DISTINCT UPPER(BTRIM(value))
                        FROM UNNEST("GroupIds") AS value
                        WHERE BTRIM(value) <> ''
                    );

                WITH ranked AS (
                    SELECT "Id",
                           ROW_NUMBER() OVER (
                               PARTITION BY "ResourceType", "ResourceId", "PrincipalType", UPPER(BTRIM("PrincipalId"))
                               ORDER BY "Role", "CreatedAtUtc", "Id"
                           ) AS row_number
                    FROM "DirectoryShareGrants"
                )
                DELETE FROM "DirectoryShareGrants" AS target
                USING ranked
                WHERE target."Id" = ranked."Id" AND ranked.row_number > 1;

                UPDATE "DirectoryShareGrants"
                SET "PrincipalId" = UPPER(BTRIM("PrincipalId"));
                """);

            migrationBuilder.DropIndex(
                name: "IX_DirectoryIdentities_PrincipalId",
                table: "DirectoryIdentities");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryIdentities_PrincipalId",
                table: "DirectoryIdentities",
                column: "PrincipalId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_DirectoryIdentities_PrincipalId",
                table: "DirectoryIdentities");

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryIdentities_PrincipalId",
                table: "DirectoryIdentities",
                column: "PrincipalId");
        }
    }
}
