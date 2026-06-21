using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class ProviderCostPriceBook : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "providers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AuthModel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "api-key"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_providers", x => x.Id);
                    table.UniqueConstraint("AK_providers_Key", x => x.Key);
                    table.CheckConstraint("ck_providers_auth_model", "\"AuthModel\" IN ('api-key','cli-token')");
                    table.CheckConstraint("ck_providers_status", "\"Status\" IN ('active','retired')");
                });

            migrationBuilder.CreateTable(
                name: "provider_model_prices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProviderKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    InputUsdPer1M = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                    OutputUsdPer1M = table.Column<decimal>(type: "numeric(20,8)", nullable: false),
                    CacheReadUsdPer1M = table.Column<decimal>(type: "numeric(20,8)", nullable: true),
                    CacheWriteUsdPer1M = table.Column<decimal>(type: "numeric(20,8)", nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "seed"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_model_prices", x => x.Id);
                    table.CheckConstraint("ck_provider_model_prices_source", "\"Source\" IN ('seed','admin')");
                    table.CheckConstraint("ck_provider_model_prices_status", "\"Status\" IN ('active','superseded')");
                    table.ForeignKey(
                        name: "FK_provider_model_prices_providers_ProviderKey",
                        column: x => x.ProviderKey,
                        principalTable: "providers",
                        principalColumn: "Key",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_model_prices_Window",
                table: "provider_model_prices",
                columns: new[] { "ProviderKey", "Model", "EffectiveFrom" });

            migrationBuilder.CreateIndex(
                name: "UX_provider_model_prices_OneActivePerModel",
                table: "provider_model_prices",
                columns: new[] { "ProviderKey", "Model" },
                unique: true,
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "UX_providers_Key",
                table: "providers",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_model_prices");

            migrationBuilder.DropTable(
                name: "providers");
        }
    }
}
