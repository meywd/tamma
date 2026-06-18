using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class AddBillingCustomerAndPlanPrices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeCustomerId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    BillingMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "PlatformProvided"),
                    DefaultCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "usd"),
                    TaxStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "none"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_customers", x => x.Id);
                    table.CheckConstraint("ck_billing_customers_mode", "\"BillingMode\" IN ('PlatformProvided','Byok')");
                    table.ForeignKey(
                        name: "FK_billing_customers_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "billing_plan_prices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlanSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StripeProductId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StripePriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TokensInputMeterId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TokensInputPriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TokensOutputMeterId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TokensOutputPriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SeatsMeterId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SeatsPriceId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_plan_prices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "UX_billing_customers_StripeCustomerId",
                table: "billing_customers",
                column: "StripeCustomerId",
                unique: true,
                filter: "\"StripeCustomerId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_billing_customers_TenantId",
                table: "billing_customers",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_billing_plan_prices_PlanSlug",
                table: "billing_plan_prices",
                column: "PlanSlug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_customers");

            migrationBuilder.DropTable(
                name: "billing_plan_prices");
        }
    }
}
