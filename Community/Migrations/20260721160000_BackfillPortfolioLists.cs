using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Klassenbibliothek.Data;

#nullable disable

namespace TodoSuite.Server.Migrations;

/// <summary>
/// Übernimmt die vor Einführung von PortfolioLists in den persönlichen
/// Navigationspräferenzen gespeicherten Portfolio-Zuordnungen.
/// </summary>
[DbContext(typeof(ApplicationDbContext))]
[Migration("20260721160000_BackfillPortfolioLists")]
public sealed class BackfillPortfolioLists : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "ListParticipants"
            SET "DirectRole" = "Role",
                "DirectInvitationPending" = "InvitationPending"
            WHERE "SourcePortfolioGroupId" IS NULL
              AND "DirectRole" IS NULL
              AND "PortfolioRole" IS NULL;

            UPDATE "ListParticipants"
            SET "PortfolioRole" = "Role",
                "DirectInvitationPending" = FALSE
            WHERE "SourcePortfolioGroupId" IS NOT NULL
              AND "PortfolioRole" IS NULL;

            INSERT INTO "PortfolioLists"
                ("Id", "PortfolioGroupId", "ListId", "SortOrder", "AddedByUserId", "CreatedAtUtc", "UpdatedAtUtc")
            SELECT gen_random_uuid(),
                   preference."NavigationGroupId",
                   preference."ListId",
                   preference."NavigationSortOrder",
                   portfolio."OwnerId",
                   NOW(),
                   NOW()
            FROM "TodoListNavigationPreferences" preference
            INNER JOIN "TodoListGroups" portfolio
                ON portfolio."Id" = preference."NavigationGroupId"
               AND portfolio."IsPortfolio" = TRUE
               AND portfolio."OwnerId" = preference."UserId"
            INNER JOIN "TodoLists" listEntity
                ON listEntity."Id" = preference."ListId"
               AND listEntity."DeletedAt" IS NULL
            ON CONFLICT ("ListId") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "PortfolioLists";
            UPDATE "ListParticipants"
            SET "DirectRole" = NULL,
                "DirectInvitationPending" = FALSE,
                "PortfolioRole" = NULL;
            """);
    }
}
