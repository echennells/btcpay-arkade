using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BTCPayServer.Plugins.ArkPayServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVtxoMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Column may already exist from earlier asset-support branch work
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'BTCPayServer.Plugins.Ark'
                          AND table_name = 'Vtxos'
                          AND column_name = 'Metadata'
                    ) THEN
                        ALTER TABLE ""BTCPayServer.Plugins.Ark"".""Vtxos""
                            ADD COLUMN ""Metadata"" jsonb;
                    END IF;
                END $$;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Metadata",
                schema: "BTCPayServer.Plugins.Ark",
                table: "Vtxos");
        }
    }
}
