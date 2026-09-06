using Klassenbibliothek.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260906110000_ReplaceFormsViewWithTimeline")]
public sealed class ReplaceFormsViewWithTimeline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Numeric value 4 previously represented the form designer. It is now an
        // administrative list option, so stale defaults must not silently open the
        // new timeline which deliberately reuses that released enum slot.
        migrationBuilder.Sql("UPDATE \"TodoLists\" SET \"DefaultView\" = 0 WHERE \"DefaultView\" = 4;");
        migrationBuilder.Sql("UPDATE \"ListViewPreferences\" SET \"LastView\" = 0 WHERE \"LastView\" = 4;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The previous form-designer preference cannot be reconstructed after it
        // has intentionally been normalized to the list view.
    }
}
