using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddDocumentInstances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "document_instances",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issue_id = table.Column<string>(type: "text", nullable: false),
                    produced_by_role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    produced_by_action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    produced_by_workflow = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    correlation_id = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    supersedes_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    parent_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlating_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_instances", x => x.id);
                    table.CheckConstraint("ck_document_instances_status", "status IN ('draft','validated','in_review','accepted','rejected','superseded','escalated')");
                    table.ForeignKey(
                        name: "FK_document_instances_document_instances_supersedes_document_id",
                        column: x => x.supersedes_document_id,
                        principalTable: "document_instances",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_document_instances_issue_created",
                table: "document_instances",
                columns: new[] { "issue_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_document_instances_issue_type_status",
                table: "document_instances",
                columns: new[] { "issue_id", "document_type", "status" });

            migrationBuilder.CreateIndex(
                name: "UX_document_instances_supersedes",
                table: "document_instances",
                column: "supersedes_document_id",
                unique: true,
                filter: "supersedes_document_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "document_instances");
        }
    }
}
