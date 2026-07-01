using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class DropCranlTenantColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
        }
    }
}
