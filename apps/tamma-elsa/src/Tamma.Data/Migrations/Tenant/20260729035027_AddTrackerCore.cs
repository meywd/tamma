using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class AddTrackerCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "projects",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Key = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    RepositoryId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstimateScale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "not_used"),
                    NextNumber = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    ArchivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_projects", x => x.Id);
                    table.CheckConstraint("ck_projects_estimate_scale", "\"EstimateScale\" IN ('not_used','linear','fibonacci','exponential','t_shirt')");
                    table.CheckConstraint("ck_projects_next_number", "\"NextNumber\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "tracker_preferences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultProjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    BoardGroupBy = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tracker_preferences", x => x.Id);
                    table.CheckConstraint("ck_tracker_preferences_default_kind", "\"DefaultKind\" IS NULL OR \"DefaultKind\" IN ('epic','story','task','spike')");
                    table.CheckConstraint("ck_tracker_preferences_principal_xor", "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "iterations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "planned"),
                    CapacityPoints = table.Column<decimal>(type: "numeric", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_iterations", x => x.Id);
                    table.CheckConstraint("ck_iterations_status", "\"Status\" IN ('planned','active','closed')");
                    table.ForeignKey(
                        name: "FK_iterations_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "work_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "text", nullable: false),
                    PreviousKeys = table.Column<List<string>>(type: "text[]", nullable: false, defaultValueSql: "'{}'::text[]"),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Priority = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    IssueType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    IterationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rank = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    SiblingRank = table.Column<string>(type: "text", nullable: false, collation: "C"),
                    AssigneeUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Estimate = table.Column<decimal>(type: "numeric", nullable: true),
                    ExternalRefJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_items", x => x.Id);
                    table.CheckConstraint("ck_work_items_issue_type", "\"IssueType\" IS NULL OR \"IssueType\" IN ('bug','feature','chore','question','security','docs')");
                    table.CheckConstraint("ck_work_items_kind", "\"Kind\" IN ('epic','story','task','spike')");
                    table.CheckConstraint("ck_work_items_number", "\"Number\" >= 1");
                    table.CheckConstraint("ck_work_items_priority", "\"Priority\" IS NULL OR \"Priority\" IN ('urgent','high','normal','low')");
                    table.CheckConstraint("ck_work_items_status", "\"Status\" IN ('triage','backlog','ready','in_progress','in_review','blocked','done','cancelled')");
                    table.ForeignKey(
                        name: "FK_work_items_iterations_IterationId",
                        column: x => x.IterationId,
                        principalTable: "iterations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_work_items_projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_items_work_items_ParentId",
                        column: x => x.ParentId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "work_item_relations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_item_relations", x => x.Id);
                    table.CheckConstraint("ck_work_item_relations_kind", "\"Kind\" IN ('blocks','duplicate','related')");
                    table.CheckConstraint("ck_work_item_relations_no_self", "\"SourceId\" <> \"TargetId\"");
                    table.ForeignKey(
                        name: "FK_work_item_relations_work_items_SourceId",
                        column: x => x.SourceId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_work_item_relations_work_items_TargetId",
                        column: x => x.TargetId,
                        principalTable: "work_items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "UX_iterations_project_name",
                table: "iterations",
                columns: new[] { "ProjectId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_projects_key",
                table: "projects",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_tracker_preferences_principal",
                table: "tracker_preferences",
                columns: new[] { "UserId", "TenantId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_work_item_relations_target",
                table: "work_item_relations",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "UX_work_item_relations_source_target_kind",
                table: "work_item_relations",
                columns: new[] { "SourceId", "TargetId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_work_items_assignee_status",
                table: "work_items",
                columns: new[] { "AssigneeUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_work_items_iteration",
                table: "work_items",
                column: "IterationId");

            migrationBuilder.CreateIndex(
                name: "IX_work_items_ParentId",
                table: "work_items",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_work_items_previous_keys",
                table: "work_items",
                column: "PreviousKeys")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_work_items_project_parent_sibling_rank",
                table: "work_items",
                columns: new[] { "ProjectId", "ParentId", "SiblingRank" });

            migrationBuilder.CreateIndex(
                name: "IX_work_items_project_status_rank",
                table: "work_items",
                columns: new[] { "ProjectId", "Status", "Rank" });

            migrationBuilder.CreateIndex(
                name: "UX_work_items_key",
                table: "work_items",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_work_items_project_number",
                table: "work_items",
                columns: new[] { "ProjectId", "Number" },
                unique: true);

            // Story 44-8's already-linked import skip: an expression index over
            // the jsonb ExternalRef keys the EF model cannot express (same
            // raw-SQL pattern as AddDomainEventsUserIdIndex — the model is
            // deliberately unchanged so `has-pending-model-changes` stays
            // clean). Partial: native items (ExternalRefJson IS NULL) are the
            // overwhelming majority and never probe this index. The
            // unqualified table name resolves to the tenant schema via the
            // per-tenant connection's search_path.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_work_items_external_ref
                  ON work_items ((("ExternalRefJson"->>'repoFullName')), (("ExternalRefJson"->>'number')))
                  WHERE "ExternalRefJson" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_work_items_external_ref;");

            migrationBuilder.DropTable(
                name: "tracker_preferences");

            migrationBuilder.DropTable(
                name: "work_item_relations");

            migrationBuilder.DropTable(
                name: "work_items");

            migrationBuilder.DropTable(
                name: "iterations");

            migrationBuilder.DropTable(
                name: "projects");
        }
    }
}
