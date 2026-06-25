using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Api.Services.Secrets.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddOnePendingPerSecretIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_secret_versions_OnePendingPerSecret",
                table: "secret_versions",
                column: "SecretId",
                unique: true,
                filter: "\"Status\" = 'pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_secret_versions_OnePendingPerSecret",
                table: "secret_versions");
        }
    }
}
