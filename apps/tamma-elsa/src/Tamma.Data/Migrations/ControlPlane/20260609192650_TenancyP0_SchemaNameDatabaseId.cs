using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class TenancyP0_SchemaNameDatabaseId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DatabaseId",
                table: "tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SchemaName",
                table: "tenants",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "tenant_databases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false, defaultValue: 5432),
                    AdminConnectionStringEncrypted = table.Column<byte[]>(type: "bytea", nullable: false),
                    PlacementClass = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "shared"),
                    TierEligibility = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    TenantCapacity = table.Column<int>(type: "integer", nullable: true),
                    TenantCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    KekVersion = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_databases", x => x.Id);
                    table.CheckConstraint("ck_tenant_databases_placement_class", "\"PlacementClass\" IN ('shared','dedicated')");
                    table.CheckConstraint("ck_tenant_databases_status", "\"Status\" IN ('active','draining','full','retired')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_DatabaseId",
                table: "tenants",
                column: "DatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_SchemaName",
                table: "tenants",
                column: "SchemaName",
                unique: true,
                filter: "\"SchemaName\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_connection_string_present",
                table: "tenants",
                sql: "\"Status\" IS NULL OR \"Status\" IN ('pending_verification','provisioning','failed','deleted') OR \"EncryptedConnectionString\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants",
                sql: "\"Status\" IS NULL OR \"Status\" IN ('pending_verification','provisioning','active','delete_requested','deleting','deleted','failed','suspended')");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_databases_Label",
                table: "tenant_databases",
                column: "Label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_databases_Status",
                table: "tenant_databases",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_tenant_databases_DatabaseId",
                table: "tenants",
                column: "DatabaseId",
                principalTable: "tenant_databases",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_tenants_tenant_databases_DatabaseId",
                table: "tenants");

            migrationBuilder.DropTable(
                name: "tenant_databases");

            migrationBuilder.DropIndex(
                name: "IX_tenants_DatabaseId",
                table: "tenants");

            migrationBuilder.DropIndex(
                name: "IX_tenants_SchemaName",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_connection_string_present",
                table: "tenants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenants_status",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "DatabaseId",
                table: "tenants");

            migrationBuilder.DropColumn(
                name: "SchemaName",
                table: "tenants");
        }
    }
}
