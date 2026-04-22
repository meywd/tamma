using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations
{
    /// <summary>
    /// Adds Cranl per-tenant provisioning columns to the <c>tenants</c>
    /// table (audit cranl/001 — Doc 02 §3). When
    /// <c>cranl_database_url_encrypted IS NOT NULL</c> the tenant rides on
    /// per-tenant Cranl infrastructure; otherwise it shares the central
    /// Postgres via RLS. <see cref="ITenantConnectionResolver"/> consumes
    /// the column when it lands.
    /// </summary>
    public partial class CranlProvisioningColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CranlAppId",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CranlAppUrl",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CranlDatabaseId",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "CranlDatabaseUrlEncrypted",
                table: "tenants",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CranlProjectId",
                table: "tenants",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CranlRegion",
                table: "tenants",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisioningDetail",
                table: "tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisioningState",
                table: "tenants",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisioningUpdatedAt",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CranlAppId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CranlAppUrl",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CranlDatabaseId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CranlDatabaseUrlEncrypted",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CranlProjectId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "CranlRegion",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProvisioningDetail",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProvisioningState",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "ProvisioningUpdatedAt",
                table: "tenants");
        }
    }
}
