using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddDocumentInstanceAudience : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "audience",
                table: "document_instances",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_instances_issue_audience",
                table: "document_instances",
                columns: new[] { "issue_id", "audience" },
                filter: "audience IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_document_instances_issue_audience",
                table: "document_instances");

            migrationBuilder.DropColumn(
                name: "audience",
                table: "document_instances");
        }
    }
}
