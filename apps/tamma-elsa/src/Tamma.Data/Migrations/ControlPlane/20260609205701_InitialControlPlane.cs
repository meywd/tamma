using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <inheritdoc />
    public partial class InitialControlPlane : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_channels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    channel_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Config = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CredentialsSecretId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_channels", x => x.Id);
                    table.CheckConstraint("CK_alert_channels_channel_type", "channel_type IN ('email','slack','pagerduty','webhook')");
                });

            migrationBuilder.CreateTable(
                name: "alert_evaluator_cursor",
                columns: table => new
                {
                    EvaluatorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LastDomainSequenceNumber = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    LastPlatformSequenceNumber = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_evaluator_cursor", x => x.EvaluatorId);
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EventType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Predicate = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    ThrottleSeconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ChannelIds = table.Column<Guid[]>(type: "uuid[]", nullable: false, defaultValueSql: "ARRAY[]::uuid[]"),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    BuiltInKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.Id);
                    table.CheckConstraint("CK_alert_rules_severity", "\"Severity\" IN ('critical','warning','info')");
                });

            migrationBuilder.CreateTable(
                name: "alerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "active"),
                    AcknowledgedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resolution = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alerts", x => x.Id);
                    table.CheckConstraint("CK_alerts_severity", "\"Severity\" IN ('critical','warning','info')");
                    table.CheckConstraint("CK_alerts_status", "\"Status\" IN ('active','acknowledged','resolved')");
                });

            migrationBuilder.CreateTable(
                name: "github_installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    InstallationId = table.Column<long>(type: "bigint", nullable: false),
                    AccountLogin = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AccountType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    AppId = table.Column<long>(type: "bigint", nullable: false),
                    AppSlug = table.Column<string>(type: "text", nullable: true),
                    Permissions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    SuspendedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_installations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_deliveries",
                columns: table => new
                {
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstallationId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_deliveries", x => x.DeliveryId);
                });

            migrationBuilder.CreateTable(
                name: "kek_rotations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VersionOld = table.Column<int>(type: "integer", nullable: false),
                    VersionNew = table.Column<int>(type: "integer", nullable: false),
                    StagedSecondaryProtected = table.Column<byte[]>(type: "bytea", nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kek_rotations", x => x.Id);
                    table.CheckConstraint("CK_kek_rotations_status", "\"Status\" IN ('pending','running','completed','failed','cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "plans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MonthlyPriceUsd = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Quotas = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    PlacementPolicy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "shared"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                    table.CheckConstraint("ck_plans_placement_policy", "\"PlacementPolicy\" IN ('shared','dedicated')");
                });

            migrationBuilder.CreateTable(
                name: "platform_analytics_hourly",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Hour = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkflowsStarted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsCompleted = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    WorkflowsFailed = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    AgentDispatches = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensIn = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    TokensOut = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    CostUsd = table.Column<decimal>(type: "numeric(20,4)", precision: 20, scale: 4, nullable: false, defaultValue: 0m),
                    ActiveTenantsAtHourEnd = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ComputedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_analytics_hourly", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_api_key_index",
                columns: table => new
                {
                    KeyPrefix = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    HashedSuffix = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_api_key_index", x => x.KeyPrefix);
                });

            migrationBuilder.CreateTable(
                name: "platform_email_outbox",
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
                    table.PrimaryKey("PK_platform_email_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Tags = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    Data = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_queued_tasks",
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
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ClaimedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UnprocessableAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_queued_tasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_webhook_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    PlatformKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DeliveryId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    EventType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InstallationExternalId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_webhook_deliveries", x => x.Id);
                    table.CheckConstraint("CK_platform_webhook_deliveries_PlatformKind", "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
                });

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

            migrationBuilder.CreateTable(
                name: "tenant_platform_installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    InstallationExternalId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CredentialSecretScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "tenant"),
                    CredentialSecretName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    WebhookSecretScope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    WebhookSecretName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "connected"),
                    IsPrimary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Metadata = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_platform_installations", x => x.Id);
                    table.CheckConstraint("CK_tenant_platform_installations_CredentialSecretScope", "\"CredentialSecretScope\" IN ('platform','tenant')");
                    table.CheckConstraint("CK_tenant_platform_installations_PlatformKind", "\"PlatformKind\" IN ('github','gitea','forgejo','gitlab','bitbucket','azure_devops')");
                    table.CheckConstraint("CK_tenant_platform_installations_Status", "\"Status\" IN ('connected','suspended','disconnected')");
                    table.CheckConstraint("CK_tenant_platform_installations_WebhookSecretScope", "\"WebhookSecretScope\" IS NULL OR \"WebhookSecretScope\" IN ('platform','tenant')");
                });

            migrationBuilder.CreateTable(
                name: "alert_delivery_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    AlertId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "pending"),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_delivery_attempts", x => x.Id);
                    table.CheckConstraint("CK_alert_delivery_attempts_status", "\"Status\" IN ('pending','success','failed','dropped_rate_limit')");
                    table.ForeignKey(
                        name: "FK_alert_delivery_attempts_alert_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "alert_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_alert_delivery_attempts_alerts_AlertId",
                        column: x => x.AlertId,
                        principalTable: "alerts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_installation_repos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    InstallationEntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    RepoId = table.Column<long>(type: "bigint", nullable: false),
                    Owner = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RepoFullName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_installation_repos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_github_installation_repos_github_installations_Installation~",
                        column: x => x.InstallationEntityId,
                        principalTable: "github_installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "admin_impersonations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    ImpersonatorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImpersonatorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TargetTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_impersonations", x => x.Id);
                    table.CheckConstraint("chk_impersonation_reason_charset", "\"Reason\" ~ '^[A-Za-z0-9 .,;:_!@#$%&()\\-]{1,500}$'");
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
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    LastUsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RotatedFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    EncryptedPlaintext = table.Column<byte[]>(type: "bytea", nullable: true),
                    RateLimitRpm = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.Id);
                    table.CheckConstraint("ck_api_keys_scope", "\"Scope\" IN ('platform','user','installation','service','tenant')");
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_bootstrap",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_bootstrap", x => x.Id);
                    table.CheckConstraint("ck_platform_bootstrap_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    JtiChainHead = table.Column<Guid>(type: "uuid", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.CheckConstraint("CK_refresh_tokens_RevokedReason", "\"RevokedReason\" IS NULL OR \"RevokedReason\" IN ('manual_logout','logout_all','rotation_consumed','switch_org','reuse_detected','password_reset','admin_force_logout')");
                    table.CheckConstraint("CK_refresh_tokens_RevokedReason_NullParity", "(\"RevokedAt\" IS NULL) = (\"RevokedReason\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "tenant_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_memberships", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "personal"),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExternalId = table.Column<string>(type: "text", nullable: true),
                    Plan = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "free"),
                    Settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CranlProjectId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CranlDatabaseId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CranlAppId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CranlRegion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CranlDatabaseUrlEncrypted = table.Column<byte[]>(type: "bytea", nullable: true),
                    CranlAppUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProvisioningState = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false, defaultValue: "none"),
                    ProvisioningDetail = table.Column<string>(type: "text", nullable: true),
                    ProvisioningUpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DatabaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeleteRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EncryptedConnectionString = table.Column<byte[]>(type: "bytea", nullable: true),
                    FailureReason = table.Column<string>(type: "text", nullable: true),
                    KekVersion = table.Column<short>(type: "smallint", nullable: false, defaultValue: (short)1),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ProviderResourceIds = table.Column<string>(type: "jsonb", nullable: true),
                    SchemaName = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.Id);
                    table.CheckConstraint("ck_tenants_connection_string_present", "\"Status\" IS NULL OR \"Status\" IN ('pending_verification','provisioning','failed','deleted') OR \"EncryptedConnectionString\" IS NOT NULL");
                    table.CheckConstraint("ck_tenants_status", "\"Status\" IS NULL OR \"Status\" IN ('pending_verification','provisioning','active','delete_requested','deleting','deleted','failed','suspended')");
                    table.ForeignKey(
                        name: "FK_tenants_plans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenants_tenant_databases_DatabaseId",
                        column: x => x.DatabaseId,
                        principalTable: "tenant_databases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_invites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),
                    InviteTokenHash = table.Column<string>(type: "text", nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_invites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_invites_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    AvatarUrl = table.Column<string>(type: "text", nullable: true),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "member"),
                    platform_role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "user"),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AuthMethod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "email"),
                    GitHubId = table.Column<long>(type: "bigint", nullable: true),
                    GitHubLogin = table.Column<string>(type: "text", nullable: true),
                    EmailVerificationTokenHash = table.Column<string>(type: "text", nullable: true),
                    EmailVerificationExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastActiveAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Settings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.CheckConstraint("ck_users_platform_role", "\"platform_role\" IN ('user','platform_admin')");
                    table.ForeignKey(
                        name: "FK_users_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_active",
                table: "admin_impersonations",
                column: "EndedAt",
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_impersonator",
                table: "admin_impersonations",
                column: "ImpersonatorUserId");

            migrationBuilder.CreateIndex(
                name: "idx_admin_impersonations_target_tenant",
                table: "admin_impersonations",
                column: "TargetTenantId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_impersonations_TargetUserId",
                table: "admin_impersonations",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_channels_ChannelType_IsEnabled",
                table: "alert_channels",
                columns: new[] { "channel_type", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_channels_TenantId_IsEnabled",
                table: "alert_channels",
                columns: new[] { "TenantId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_delivery_attempts_AlertId",
                table: "alert_delivery_attempts",
                column: "AlertId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_delivery_attempts_ChannelId",
                table: "alert_delivery_attempts",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_delivery_attempts_Status_CreatedAt",
                table: "alert_delivery_attempts",
                columns: new[] { "Status", "CreatedAt" },
                descending: new[] { false, true },
                filter: "\"Status\" IN ('pending','failed')");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_EventType_IsEnabled",
                table: "alert_rules",
                columns: new[] { "EventType", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "UX_alert_rules_BuiltInKey",
                table: "alert_rules",
                column: "BuiltInKey",
                unique: true,
                filter: "\"BuiltInKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_alert_rules_Name",
                table: "alert_rules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alerts_CorrelationId",
                table: "alerts",
                column: "CorrelationId",
                filter: "\"CorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_alerts_Severity_CreatedAt",
                table: "alerts",
                columns: new[] { "Severity", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_Status_CreatedAt",
                table: "alerts",
                columns: new[] { "Status", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_alerts_TenantId_CreatedAt",
                table: "alerts",
                columns: new[] { "TenantId", "CreatedAt" },
                descending: new[] { false, true },
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
                name: "IX_api_keys_TenantId",
                table: "api_keys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_UserId",
                table: "api_keys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_github_installation_repos_InstallationEntityId_RepoId",
                table: "github_installation_repos",
                columns: new[] { "InstallationEntityId", "RepoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_installation_repos_RepoFullName",
                table: "github_installation_repos",
                column: "RepoFullName");

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_AccountLogin",
                table: "github_installations",
                column: "AccountLogin");

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_InstallationId",
                table: "github_installations",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_installations_TenantId",
                table: "github_installations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_InstallationId_ReceivedAt",
                table: "github_webhook_deliveries",
                columns: new[] { "InstallationId", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_github_webhook_deliveries_ReceivedAt",
                table: "github_webhook_deliveries",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_kek_rotations_StartedAt",
                table: "kek_rotations",
                column: "StartedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_kek_rotations_Status",
                table: "kek_rotations",
                column: "Status",
                filter: "\"Status\" IN ('pending','running')");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_TokenHash",
                table: "password_reset_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_UserId",
                table: "password_reset_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_plans_Slug",
                table: "plans",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_analytics_hourly_TenantId_Hour",
                table: "platform_analytics_hourly",
                columns: new[] { "TenantId", "Hour" },
                descending: new[] { false, true },
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_platform_analytics_hourly_Hour_PlatformWide",
                table: "platform_analytics_hourly",
                column: "Hour",
                unique: true,
                descending: new bool[0],
                filter: "\"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_platform_analytics_hourly_Hour_TenantId",
                table: "platform_analytics_hourly",
                columns: new[] { "Hour", "TenantId" },
                unique: true,
                descending: new[] { true, false },
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_ApiKeyId",
                table: "platform_api_key_index",
                column: "ApiKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_KeyPrefix_HashedSuffix",
                table: "platform_api_key_index",
                columns: new[] { "KeyPrefix", "HashedSuffix" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_api_key_index_TenantId",
                table: "platform_api_key_index",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_bootstrap_UserId",
                table: "platform_bootstrap",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_email_outbox_Status_NextAttemptAt",
                table: "platform_email_outbox",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "UX_platform_email_outbox_tenant_template_active",
                table: "platform_email_outbox",
                columns: new[] { "TenantId", "Template" },
                unique: true,
                filter: "\"Status\" <> 'failed' AND \"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_CreatedAt",
                table: "platform_events",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_TenantId",
                table: "platform_events",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_Type_CreatedAt",
                table: "platform_events",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_events_UserId",
                table: "platform_events",
                column: "UserId",
                filter: "\"UserId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_platform_events_SequenceNumber",
                table: "platform_events",
                column: "SequenceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_InstallationId",
                table: "platform_queued_tasks",
                column: "InstallationId",
                filter: "\"InstallationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_Status_CreatedAt",
                table: "platform_queued_tasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_queued_tasks_TenantId",
                table: "platform_queued_tasks",
                column: "TenantId",
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_platform_webhook_deliveries_ReceivedAt",
                table: "platform_webhook_deliveries",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "UX_platform_webhook_deliveries_Kind_DeliveryId",
                table: "platform_webhook_deliveries",
                columns: new[] { "PlatformKind", "DeliveryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_JtiChainHead",
                table: "refresh_tokens",
                column: "JtiChainHead",
                filter: "\"JtiChainHead\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId_TenantId",
                table: "refresh_tokens",
                columns: new[] { "UserId", "TenantId" },
                filter: "\"TenantId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_databases_Label",
                table: "tenant_databases",
                column: "Label",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_databases_Status",
                table: "tenant_databases",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_TenantId_UserId",
                table: "tenant_memberships",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_memberships_UserId",
                table: "tenant_memberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_platform_installations_PlatformKind_ExternalId",
                table: "tenant_platform_installations",
                columns: new[] { "PlatformKind", "InstallationExternalId" },
                filter: "\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_tenant_platform_installations_PrimaryPerKind",
                table: "tenant_platform_installations",
                columns: new[] { "TenantId", "PlatformKind" },
                unique: true,
                filter: "\"IsPrimary\" = TRUE AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_tenant_platform_installations_TenantId_Kind_ExternalId",
                table: "tenant_platform_installations",
                columns: new[] { "TenantId", "PlatformKind", "InstallationExternalId" },
                unique: true,
                filter: "\"InstallationExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_DatabaseId",
                table: "tenants",
                column: "DatabaseId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_ExternalId",
                table: "tenants",
                column: "ExternalId",
                unique: true,
                filter: "\"ExternalId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_OwnerId",
                table: "tenants",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_PlanId",
                table: "tenants",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_SchemaName",
                table: "tenants",
                column: "SchemaName",
                unique: true,
                filter: "\"SchemaName\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Slug",
                table: "tenants",
                column: "Slug",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_tenants_Status",
                table: "tenants",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_user_invites_InviteTokenHash",
                table: "user_invites",
                column: "InviteTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_invites_TenantId",
                table: "user_invites",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_GitHubId",
                table: "users",
                column: "GitHubId",
                unique: true,
                filter: "\"GitHubId\" IS NOT NULL AND \"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_users_TenantId",
                table: "users",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_admin_impersonations_tenants_TargetTenantId",
                table: "admin_impersonations",
                column: "TargetTenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_admin_impersonations_users_ImpersonatorUserId",
                table: "admin_impersonations",
                column: "ImpersonatorUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_admin_impersonations_users_TargetUserId",
                table: "admin_impersonations",
                column: "TargetUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_api_keys_users_UserId",
                table: "api_keys",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_password_reset_tokens_users_UserId",
                table: "password_reset_tokens",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_platform_bootstrap_users_UserId",
                table: "platform_bootstrap",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_refresh_tokens_users_UserId",
                table: "refresh_tokens",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_memberships_tenants_TenantId",
                table: "tenant_memberships",
                column: "TenantId",
                principalTable: "tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_tenant_memberships_users_UserId",
                table: "tenant_memberships",
                column: "UserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_tenants_users_OwnerId",
                table: "tenants",
                column: "OwnerId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ── Ported from the pre-collapse chain (unified-tenancy Phase 0) ──
            // Objects the EF model cannot express, preserved verbatim so the
            // collapsed baseline reproduces the old chain's schema exactly:
            // RLS (tamma_app role, ENABLE/FORCE, policies, prevent_tenant_id_change
            // triggers), partial/expression indexes, legacy CHECKs, and the
            // api_keys self-FK. RLS removal is deliberately deferred to
            // unified-tenancy Phase 5 — Phase 0 is behavior-neutral.
            migrationBuilder.Sql("""
                -- 1. tamma_app role (cluster-level, idempotent) + grants.
                --    Phase-3 RLS design: tamma_app is the future runtime role;
                --    policies below stay dormant until the connection string
                --    switches to it. Password is a placeholder; production
                --    overrides via ALTER ROLE.
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'tamma_app') THEN
                    CREATE ROLE tamma_app LOGIN PASSWORD 'changeme';
                  END IF;
                END $$;

                DO $$
                BEGIN
                  EXECUTE format('GRANT CONNECT ON DATABASE %I TO tamma_app', current_database());
                END $$;

                GRANT USAGE ON SCHEMA public TO tamma_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO tamma_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO tamma_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO tamma_app;
                ALTER DEFAULT PRIVILEGES IN SCHEMA public
                  GRANT USAGE, SELECT ON SEQUENCES TO tamma_app;

                -- 2. prevent_tenant_id_change trigger function + BEFORE-UPDATE
                --    triggers. First NULL → uuid assignment is permitted
                --    (personal-tenant bootstrap); any later change is blocked.
                CREATE OR REPLACE FUNCTION prevent_tenant_id_change()
                RETURNS TRIGGER AS $$
                BEGIN
                  IF OLD."TenantId" IS NOT NULL
                     AND OLD."TenantId" IS DISTINCT FROM NEW."TenantId" THEN
                    RAISE EXCEPTION 'Cannot change TenantId on existing row';
                  END IF;
                  RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                DROP TRIGGER IF EXISTS trg_prevent_tenant_change_users ON users;
                CREATE TRIGGER trg_prevent_tenant_change_users
                  BEFORE UPDATE ON users
                  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

                DROP TRIGGER IF EXISTS trg_prevent_tenant_change_github_installations ON github_installations;
                CREATE TRIGGER trg_prevent_tenant_change_github_installations
                  BEFORE UPDATE ON github_installations
                  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

                DROP TRIGGER IF EXISTS trg_prevent_tenant_change_api_keys ON api_keys;
                CREATE TRIGGER trg_prevent_tenant_change_api_keys
                  BEFORE UPDATE ON api_keys
                  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

                DROP TRIGGER IF EXISTS trg_prevent_tenant_change_user_invites ON user_invites;
                CREATE TRIGGER trg_prevent_tenant_change_user_invites
                  BEFORE UPDATE ON user_invites
                  FOR EACH ROW EXECUTE FUNCTION prevent_tenant_id_change();

                -- 3. RLS ENABLE + FORCE on the seven CP tenant-scoped tables.
                ALTER TABLE tenants ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenants FORCE ROW LEVEL SECURITY;
                ALTER TABLE tenant_memberships ENABLE ROW LEVEL SECURITY;
                ALTER TABLE tenant_memberships FORCE ROW LEVEL SECURITY;
                ALTER TABLE users ENABLE ROW LEVEL SECURITY;
                ALTER TABLE users FORCE ROW LEVEL SECURITY;
                ALTER TABLE github_installations ENABLE ROW LEVEL SECURITY;
                ALTER TABLE github_installations FORCE ROW LEVEL SECURITY;
                ALTER TABLE github_installation_repos ENABLE ROW LEVEL SECURITY;
                ALTER TABLE github_installation_repos FORCE ROW LEVEL SECURITY;
                ALTER TABLE user_invites ENABLE ROW LEVEL SECURITY;
                ALTER TABLE user_invites FORCE ROW LEVEL SECURITY;
                ALTER TABLE api_keys ENABLE ROW LEVEL SECURITY;
                ALTER TABLE api_keys FORCE ROW LEVEL SECURITY;

                -- 4. RLS policies — FINAL shapes as the old chain left them
                --    (users / github_installations / user_invites carry the
                --    Phase2RlsNullPolicyTightening strict shape, no IS NULL branch).
                CREATE POLICY tenant_isolation_policy ON tenants
                  USING ("Id" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                  WITH CHECK ("Id" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

                CREATE POLICY tenant_isolation_policy ON tenant_memberships
                  USING ("TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid)
                  WITH CHECK ("TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid);

                CREATE POLICY tenant_isolation_policy ON github_installation_repos
                  USING (
                    EXISTS (
                      SELECT 1 FROM github_installations gi
                      WHERE gi."Id" = github_installation_repos."InstallationEntityId"
                        AND (
                          gi."TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                          OR gi."TenantId" IS NULL
                        )
                    )
                  );

                CREATE POLICY tenant_isolation_policy ON api_keys
                  USING (
                    "Scope" = 'service'
                    OR "TenantId" IS NULL
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  )
                  WITH CHECK (
                    "Scope" = 'service'
                    OR "TenantId" IS NULL
                    OR "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  );

                CREATE POLICY tenant_isolation_policy ON users
                  USING (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  )
                  WITH CHECK (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  );

                CREATE POLICY tenant_isolation_policy ON github_installations
                  USING (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  )
                  WITH CHECK (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  );

                CREATE POLICY tenant_isolation_policy ON user_invites
                  USING (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  )
                  WITH CHECK (
                    "TenantId" = NULLIF(current_setting('app.current_tenant_id', true), '')::uuid
                  );

                -- 5. Partial / expression indexes the EF model cannot express.
                CREATE INDEX IF NOT EXISTS ix_refresh_tokens_active_expires
                  ON refresh_tokens ("ExpiresAt")
                  WHERE "RevokedAt" IS NULL;

                CREATE INDEX IF NOT EXISTS ix_password_reset_tokens_active_expires
                  ON password_reset_tokens ("ExpiresAt")
                  WHERE "ConsumedAt" IS NULL;

                CREATE UNIQUE INDEX IF NOT EXISTS ix_users_email_lower
                  ON users (LOWER("Email"))
                  WHERE "DeletedAt" IS NULL;

                CREATE INDEX IF NOT EXISTS ix_tenants_deleted_at
                  ON tenants ("DeletedAt")
                  WHERE "DeletedAt" IS NULL;

                CREATE INDEX IF NOT EXISTS ix_api_keys_active
                  ON api_keys ("Scope")
                  WHERE "RevokedAt" IS NULL;

                -- 6. Legacy CHECK constraints not represented in the model.
                ALTER TABLE tenants
                  ADD CONSTRAINT ck_tenants_plan
                  CHECK ("Plan" IN ('free', 'pro', 'enterprise'));

                ALTER TABLE tenant_memberships
                  ADD CONSTRAINT ck_tenant_memberships_role
                  CHECK ("Role" IN ('owner', 'admin', 'member'));

                ALTER TABLE user_invites
                  ADD CONSTRAINT ck_user_invites_role
                  CHECK ("Role" IN ('owner', 'admin', 'member'));

                ALTER TABLE users
                  ADD CONSTRAINT ck_users_role
                  CHECK ("Role" IN ('owner', 'admin', 'member'));

                ALTER TABLE users
                  ADD CONSTRAINT ck_users_auth_method
                  CHECK ("AuthMethod" IN ('email', 'github', 'both'));

                ALTER TABLE github_installations
                  ADD CONSTRAINT ck_github_installations_account_type
                  CHECK ("AccountType" IN ('User', 'Organization'));

                -- 7. api_keys self-FK (RotatedFromId has no navigation in the model).
                ALTER TABLE api_keys
                  ADD CONSTRAINT fk_api_keys_rotated_from
                  FOREIGN KEY ("RotatedFromId") REFERENCES api_keys("Id")
                  ON DELETE SET NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ported objects: everything table-scoped (policies, triggers,
            // partial indexes, CHECKs, the api_keys self-FK) dies with the
            // DropTable calls below. Only the trigger function is standalone.
            // The tamma_app role is deliberately NOT dropped: it is
            // cluster-level and may carry grants in other databases on the
            // same server — dropping a role another DB still references is
            // unsafe. Operators can remove it manually via
            // `DROP OWNED BY tamma_app; DROP ROLE tamma_app;` if needed.
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS prevent_tenant_id_change() CASCADE;");

            migrationBuilder.DropForeignKey(
                name: "FK_users_tenants_TenantId",
                table: "users");

            migrationBuilder.DropTable(
                name: "admin_impersonations");

            migrationBuilder.DropTable(
                name: "alert_delivery_attempts");

            migrationBuilder.DropTable(
                name: "alert_evaluator_cursor");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "github_installation_repos");

            migrationBuilder.DropTable(
                name: "github_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "kek_rotations");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "platform_analytics_hourly");

            migrationBuilder.DropTable(
                name: "platform_api_key_index");

            migrationBuilder.DropTable(
                name: "platform_bootstrap");

            migrationBuilder.DropTable(
                name: "platform_email_outbox");

            migrationBuilder.DropTable(
                name: "platform_events");

            migrationBuilder.DropTable(
                name: "platform_queued_tasks");

            migrationBuilder.DropTable(
                name: "platform_webhook_deliveries");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "tenant_memberships");

            migrationBuilder.DropTable(
                name: "tenant_platform_installations");

            migrationBuilder.DropTable(
                name: "user_invites");

            migrationBuilder.DropTable(
                name: "alert_channels");

            migrationBuilder.DropTable(
                name: "alerts");

            migrationBuilder.DropTable(
                name: "github_installations");

            migrationBuilder.DropTable(
                name: "tenants");

            migrationBuilder.DropTable(
                name: "plans");

            migrationBuilder.DropTable(
                name: "tenant_databases");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
