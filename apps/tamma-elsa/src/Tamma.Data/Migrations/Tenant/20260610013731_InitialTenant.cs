using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <inheritdoc />
    public partial class InitialTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OwnerId = table.Column<string>(type: "text", nullable: false),
                    KeyHash = table.Column<string>(type: "text", nullable: false),
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Label = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RotatedFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    EncryptedPlaintext = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                    table.CheckConstraint("ck_api_keys_tenant_scope", "\"Scope\" = 'tenant'");
                });

            migrationBuilder.CreateTable(
                name: "budget_configs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    LimitUsd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    AlertThreshold = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.80000000000000004),
                    PeriodDays = table.Column<int>(type: "integer", nullable: false, defaultValue: 30),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_configs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "conventions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conventions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "domain_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    IssueNumber = table.Column<int>(type: "integer", nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_domain_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Template = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ToAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    HtmlBody = table.Column<string>(type: "text", nullable: false),
                    TextBody = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "junior_developers",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    slack_id = table.Column<string>(type: "text", nullable: true),
                    github_username = table.Column<string>(type: "text", nullable: true),
                    skill_level = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    preferences = table.Column<string>(type: "jsonb", nullable: true),
                    learning_patterns = table.Column<string>(type: "jsonb", nullable: true),
                    total_sessions = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    successful_sessions = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_junior_developers", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "prompt_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Role = table.Column<string>(type: "text", nullable: true),
                    Action = table.Column<string>(type: "text", nullable: true),
                    Template = table.Column<string>(type: "text", nullable: false),
                    SystemPrompt = table.Column<string>(type: "text", nullable: true),
                    Variables = table.Column<string[]>(type: "text[]", nullable: false),
                    EnableTools = table.Column<bool>(type: "boolean", nullable: false),
                    MaxTokens = table.Column<int>(type: "integer", nullable: false, defaultValue: 4096),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prompt_overrides", x => x.Id);
                    table.CheckConstraint("ck_prompt_overrides_principal_xor", "(\"UserId\" IS NOT NULL AND \"TenantId\" IS NULL) OR (\"UserId\" IS NULL AND \"TenantId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "provider_diagnostics",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProviderKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RequestDurationMs = table.Column<double>(type: "double precision", nullable: false),
                    TokensUsed = table.Column<int>(type: "integer", nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: false),
                    OutputTokens = table.Column<int>(type: "integer", nullable: false),
                    Cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    RequestType = table.Column<string>(type: "text", nullable: true),
                    Success = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "text", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    AgentType = table.Column<string>(type: "text", nullable: true),
                    ProjectId = table.Column<string>(type: "text", nullable: true),
                    EngineId = table.Column<string>(type: "text", nullable: true),
                    TaskId = table.Column<string>(type: "text", nullable: true),
                    TaskType = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_diagnostics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "provider_health",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ProviderKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "unknown"),
                    LastSuccess = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastFailure = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureCount = table.Column<int>(type: "integer", nullable: false),
                    FailureWindowStart = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CircuitOpenUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    HalfOpenInProgress = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "queued_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    InstallationId = table.Column<long>(type: "bigint", nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    Error = table.Column<string>(type: "text", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_queued_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sanitization_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Rules = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sanitization_rules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "stories",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    acceptance_criteria = table.Column<string>(type: "jsonb", nullable: true),
                    technical_requirements = table.Column<string>(type: "jsonb", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    complexity = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    estimated_hours = table.Column<int>(type: "integer", nullable: true),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    repository_url = table.Column<string>(type: "text", nullable: true),
                    JiraTicketId = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_stories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    Steps = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    SyncedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "mentorship_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    story_id = table.Column<string>(type: "text", nullable: false),
                    junior_id = table.Column<string>(type: "text", nullable: false),
                    current_state = table.Column<string>(type: "text", nullable: false),
                    previous_state = table.Column<string>(type: "text", nullable: true),
                    context = table.Column<string>(type: "jsonb", nullable: true),
                    variables = table.Column<string>(type: "jsonb", nullable: true),
                    workflow_instance_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "Active"),
                    row_version = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mentorship_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_mentorship_sessions_junior_developers_junior_id",
                        column: x => x.junior_id,
                        principalTable: "junior_developers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_mentorship_sessions_stories_story_id",
                        column: x => x.story_id,
                        principalTable: "stories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_instances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    DefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "pending"),
                    CurrentActivity = table.Column<string>(type: "text", nullable: true),
                    Variables = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_instances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_workflow_instances_workflow_definitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mentorship_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    event_data = table.Column<string>(type: "jsonb", nullable: true),
                    state_from = table.Column<string>(type: "text", nullable: true),
                    state_to = table.Column<string>(type: "text", nullable: true),
                    trigger = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mentorship_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_mentorship_events_mentorship_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "mentorship_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_configs_TenantId",
                table: "agent_configs",
                column: "TenantId",
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyHash",
                table: "api_keys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_KeyPrefix",
                table: "api_keys",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_RevokedAt",
                table: "api_keys",
                column: "RevokedAt",
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_Scope_OwnerId",
                table: "api_keys",
                columns: new[] { "Scope", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "ix_budget_configs_accountid_default",
                table: "budget_configs",
                column: "AccountId",
                unique: true,
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_budget_configs_TenantId_AccountId",
                table: "budget_configs",
                columns: new[] { "TenantId", "AccountId" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_conventions_TenantId_Role_Action",
                table: "conventions",
                columns: new[] { "TenantId", "Role", "Action" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_domain_events_TenantId",
                table: "domain_events",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_domain_events_TenantId_IssueNumber",
                table: "domain_events",
                columns: new[] { "TenantId", "IssueNumber" },
                filter: "\"IssueNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_domain_events_Type_CreatedAt",
                table: "domain_events",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "UX_domain_events_SequenceNumber",
                table: "domain_events",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_Status_NextAttemptAt",
                table: "email_outbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_email_outbox_TenantId",
                table: "email_outbox",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_junior_developers_email",
                table: "junior_developers",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_junior_developers_github_username",
                table: "junior_developers",
                column: "github_username");

            migrationBuilder.CreateIndex(
                name: "IX_junior_developers_skill_level",
                table: "junior_developers",
                column: "skill_level");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_events_created_at",
                table: "mentorship_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_events_event_type",
                table: "mentorship_events",
                column: "event_type");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_events_session_id",
                table: "mentorship_events",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_created_at",
                table: "mentorship_sessions",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_current_state",
                table: "mentorship_sessions",
                column: "current_state");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_junior_id",
                table: "mentorship_sessions",
                column: "junior_id");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_status",
                table: "mentorship_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_story_id",
                table: "mentorship_sessions",
                column: "story_id");

            migrationBuilder.CreateIndex(
                name: "IX_mentorship_sessions_workflow_instance_id",
                table: "mentorship_sessions",
                column: "workflow_instance_id");

            migrationBuilder.CreateIndex(
                name: "IX_prompt_overrides_UserId_TenantId_Scope_Role_Action",
                table: "prompt_overrides",
                columns: new[] { "UserId", "TenantId", "Scope", "Role", "Action" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_CorrelationId",
                table: "provider_diagnostics",
                column: "CorrelationId",
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_EngineId_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "EngineId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_Model_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "Model", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_ProviderKey_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "ProviderKey", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_RequestType_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "RequestType", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_diagnostics_TenantId_CreatedAt",
                table: "provider_diagnostics",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_ProviderKey_TenantId",
                table: "provider_health",
                columns: new[] { "ProviderKey", "TenantId" },
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_queued_tasks_Status_CreatedAt",
                table: "queued_tasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_queued_tasks_TenantId_Status",
                table: "queued_tasks",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_sanitization_rules_TenantId",
                table: "sanitization_rules",
                column: "TenantId",
                unique: true,
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_stories_complexity",
                table: "stories",
                column: "complexity");

            migrationBuilder.CreateIndex(
                name: "IX_stories_priority",
                table: "stories",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definitions_TenantId",
                table: "workflow_definitions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_DefinitionId_Status",
                table: "workflow_instances",
                columns: new[] { "DefinitionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_TenantId",
                table: "workflow_instances",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_TenantId_DefinitionId",
                table: "workflow_instances",
                columns: new[] { "TenantId", "DefinitionId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_TenantId_Status",
                table: "workflow_instances",
                columns: new[] { "TenantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_configs");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "budget_configs");

            migrationBuilder.DropTable(
                name: "conventions");

            migrationBuilder.DropTable(
                name: "domain_events");

            migrationBuilder.DropTable(
                name: "email_outbox");

            migrationBuilder.DropTable(
                name: "mentorship_events");

            migrationBuilder.DropTable(
                name: "prompt_overrides");

            migrationBuilder.DropTable(
                name: "provider_diagnostics");

            migrationBuilder.DropTable(
                name: "provider_health");

            migrationBuilder.DropTable(
                name: "queued_tasks");

            migrationBuilder.DropTable(
                name: "sanitization_rules");

            migrationBuilder.DropTable(
                name: "workflow_instances");

            migrationBuilder.DropTable(
                name: "mentorship_sessions");

            migrationBuilder.DropTable(
                name: "workflow_definitions");

            migrationBuilder.DropTable(
                name: "junior_developers");

            migrationBuilder.DropTable(
                name: "stories");
        }
    }
}
