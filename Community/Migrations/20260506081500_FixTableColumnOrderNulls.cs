using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoSuite.Server.Migrations
{
    /// <inheritdoc />
    public partial class FixTableColumnOrderNulls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'TodoLists'
                          AND column_name = 'TableColumnOrder'
                    ) THEN
                        UPDATE "TodoLists"
                        SET "TableColumnOrder" = ARRAY[]::text[]
                        WHERE "TableColumnOrder" IS NULL;

                        ALTER TABLE "TodoLists"
                        ALTER COLUMN "TableColumnOrder" SET DEFAULT ARRAY[]::text[];

                        ALTER TABLE "TodoLists"
                        ALTER COLUMN "TableColumnOrder" SET NOT NULL;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'TodoLists'
                          AND column_name = 'TableColumnOrder'
                    ) THEN
                        ALTER TABLE "TodoLists"
                        ALTER COLUMN "TableColumnOrder" DROP DEFAULT;
                    END IF;
                END $$;
                """);
        }
    }
}
