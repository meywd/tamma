# Phase 1 Implementation Plan: EF Core DbContext + Auth

**Story**: 19-1 (API Consolidation from TypeScript to C#)
**Phase**: 1 of 5 — Foundation
**Estimated effort**: 40 hours
**Prerequisite**: None (this is the foundation all other phases depend on)

---

## Overview

Phase 1 establishes the data layer (EF Core entities + DbContext) and authentication
infrastructure (JWT, API key, RBAC, tenant context) that all subsequent phases build on.
No routes change hands in this phase — nginx routing is untouched.

**Source of truth**:
- 18 SQL migration files in `database/migrations/001_*.sql` through `018_*.sql`
- 13 TS auth files in `packages/api/src/auth/`
- 5 TS middleware files in `packages/api/src/middleware/`

**Target projects**:
- `apps/tamma-elsa/src/Tamma.Data/` — entities, DbContext, repositories
- `apps/tamma-elsa/src/Tamma.Api/` — middleware, auth, services
- `apps/tamma-elsa/tests/Tamma.Api.Tests/` — xUnit tests

---

## Task 1: EF Core Entity Definitions (22 entities)

Create one file per entity in `Tamma.Data/Entities/`. Each entity maps to an existing
PostgreSQL table created by the SQL migrations.

### 1.1 GitHubInstallation (migration 001 + 003 + 008)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs`

```csharp
public class GitHubInstallation
{
    public long InstallationId { get; set; }        // PK, bigint
    public string AccountLogin { get; set; }         // text NOT NULL
    public string AccountType { get; set; }          // text NOT NULL, check User|Organization
    public long AppId { get; set; }                  // bigint NOT NULL
    public JsonDocument Permissions { get; set; }    // jsonb, default '{}'
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? ApiKeyHash { get; set; }          // text (003)
    public string? ApiKeyPrefix { get; set; }        // text (003)
    public string? ApiKeyEncrypted { get; set; }     // text (003)
    public Guid TenantId { get; set; }               // uuid NOT NULL, default sentinel (008)
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; }
    public ICollection<GitHubInstallationRepo> Repos { get; set; }
    public ICollection<UserInstallation> UserInstallations { get; set; }
}
```

### 1.2 GitHubInstallationRepo (migration 001)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallationRepo.cs`

```csharp
public class GitHubInstallationRepo
{
    public long Id { get; set; }                     // PK, bigserial
    public long InstallationId { get; set; }         // FK -> github_installations
    public long RepoId { get; set; }                 // bigint NOT NULL
    public string Owner { get; set; }                // text NOT NULL
    public string Name { get; set; }                 // text NOT NULL
    public string FullName { get; set; }             // text NOT NULL
    public bool IsActive { get; set; }               // boolean, default true
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public GitHubInstallation Installation { get; set; }
}
```

### 1.3 User (migrations 002 + 004 + 007 + 008 + 018)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`

```csharp
public class User
{
    public Guid Id { get; set; }                     // PK, uuid
    public long? GitHubId { get; set; }              // bigint UNIQUE (nullable after 018)
    public string GitHubLogin { get; set; }          // text NOT NULL
    public string? Email { get; set; }               // text
    public string Role { get; set; }                 // text NOT NULL, default 'member'
    public JsonDocument Settings { get; set; }       // jsonb, default '{}' (004)
    public DateTimeOffset? DeletedAt { get; set; }   // timestamptz (007)
    public DateTimeOffset? LastActiveAt { get; set; }// timestamptz (007)
    public Guid? TenantId { get; set; }              // uuid nullable (008)
    public string? PasswordHash { get; set; }        // text (018)
    public bool EmailVerified { get; set; }          // boolean, default false (018)
    public string? EmailVerificationTokenHash { get; set; }  // text (018)
    public DateTimeOffset? EmailVerificationExpiresAt { get; set; } // (018)
    public string AuthMethod { get; set; }           // text, default 'github' (018)
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
    public ICollection<UserInstallation> UserInstallations { get; set; }
    public ICollection<UserApiKey> UserApiKeys { get; set; }
    public ICollection<TenantMembership> TenantMemberships { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; set; }
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; }
}
```

### 1.4 UserInstallation (migration 002)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInstallation.cs`

```csharp
public class UserInstallation
{
    public Guid UserId { get; set; }                 // PK part 1, FK -> users
    public long InstallationId { get; set; }         // PK part 2, FK -> github_installations
    public string Role { get; set; }                 // text, default 'member'
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public User User { get; set; }
    public GitHubInstallation Installation { get; set; }
}
```

### 1.5 UserApiKey (migration 005 + 008)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/UserApiKey.cs`

```csharp
public class UserApiKey
{
    public Guid Id { get; set; }                     // PK, uuid
    public Guid UserId { get; set; }                 // FK -> users
    public string KeyHash { get; set; }              // text UNIQUE NOT NULL
    public string KeyPrefix { get; set; }            // text NOT NULL
    public string Label { get; set; }                // text, default 'default'
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid TenantId { get; set; }               // uuid NOT NULL (008)

    // Navigation
    public User User { get; set; }
    public Tenant Tenant { get; set; }
}
```

### 1.6 UserInvite (migration 006 + 008)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs`

```csharp
public class UserInvite
{
    public Guid Id { get; set; }                     // PK, uuid
    public string? Email { get; set; }               // text
    public string Role { get; set; }                 // text, default 'member'
    public string InviteToken { get; set; }          // text UNIQUE NOT NULL
    public Guid InvitedBy { get; set; }              // FK -> users
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid TenantId { get; set; }               // uuid NOT NULL (008)

    // Navigation
    public User InvitedByUser { get; set; }
    public Tenant Tenant { get; set; }
}
```

### 1.7 Tenant (migration 008)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs`

```csharp
public class Tenant
{
    public Guid Id { get; set; }                     // PK, uuid
    public string Name { get; set; }                 // text NOT NULL
    public string Slug { get; set; }                 // text UNIQUE NOT NULL
    public string? ExternalId { get; set; }          // text UNIQUE
    public string Plan { get; set; }                 // text, default 'free'
    public JsonDocument Settings { get; set; }       // jsonb, default '{}'
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }

    // Navigation
    public ICollection<TenantMembership> Memberships { get; set; }
    public ICollection<User> Users { get; set; }
    public ICollection<GitHubInstallation> Installations { get; set; }
}
```

### 1.8 UnifiedApiKey (migration 009)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/UnifiedApiKey.cs`

```csharp
public class UnifiedApiKey
{
    public Guid Id { get; set; }                     // PK, uuid
    public string Scope { get; set; }                // text NOT NULL (user|installation|service)
    public string OwnerId { get; set; }              // text NOT NULL
    public string KeyHash { get; set; }              // text UNIQUE NOT NULL
    public string KeyPrefix { get; set; }            // text NOT NULL
    public string Label { get; set; }                // text, default 'default'
    public JsonDocument Permissions { get; set; }    // jsonb, default '[]'
    public Guid? TenantId { get; set; }              // uuid, nullable for service keys
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RotatedFrom { get; set; }           // FK -> api_keys (self-ref)

    // Navigation
    public Tenant? Tenant { get; set; }
    public UnifiedApiKey? RotatedFromKey { get; set; }
}
```

### 1.9 EngineEvent (migration 011)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/EngineEvent.cs`

```csharp
public class EngineEvent
{
    public Guid Id { get; set; }                     // PK, uuid
    public string Type { get; set; }                 // text NOT NULL
    public long Timestamp { get; set; }              // bigint (epoch ms)
    public Guid TenantId { get; set; }               // uuid NOT NULL
    public int? IssueNumber { get; set; }            // integer
    public JsonDocument Data { get; set; }           // jsonb, default '{}'
    public DateTimeOffset CreatedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; }
}
```

### 1.10 WorkflowInstance (migration 011)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowInstance.cs`

```csharp
public class WorkflowInstance
{
    public Guid Id { get; set; }                     // PK, uuid
    public string DefinitionId { get; set; }         // text NOT NULL
    public Guid TenantId { get; set; }               // uuid NOT NULL
    public string Status { get; set; }               // text, default 'pending'
    public string? CurrentActivity { get; set; }     // text
    public JsonDocument Variables { get; set; }      // jsonb, default '{}'
    public long CreatedAt { get; set; }              // bigint (epoch ms)
    public long UpdatedAt { get; set; }              // bigint (epoch ms)

    // Navigation
    public Tenant Tenant { get; set; }
}
```

### 1.11 Prompt (migration 012)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/Prompt.cs`

```csharp
public class Prompt
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }              // NULL = system default
    public string Role { get; set; }
    public string Action { get; set; }
    public string Template { get; set; }
    public string SystemPrompt { get; set; }         // default ''
    public JsonDocument Variables { get; set; }      // jsonb, default '[]'
    public bool EnableTools { get; set; }            // default false
    public int MaxTokens { get; set; }               // default 4096
    public int Version { get; set; }                 // default 1
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    // Navigation
    public Tenant? Tenant { get; set; }
}
```

### 1.12 SystemPrompt (migration 012)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/SystemPrompt.cs`

```csharp
public class SystemPrompt
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Role { get; set; }
    public string PromptText { get; set; }           // column: "prompt"
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    public Tenant? Tenant { get; set; }
}
```

### 1.13 ActionPrompt (migration 012)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/ActionPrompt.cs`

```csharp
public class ActionPrompt
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Action { get; set; }
    public string Template { get; set; }
    public JsonDocument Variables { get; set; }
    public bool EnableTools { get; set; }
    public int MaxTokens { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    public Tenant? Tenant { get; set; }
}
```

### 1.14 AgentConfig (migration 013)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/AgentConfig.cs`

```csharp
public class AgentConfig
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }             // NULL = system default
    public JsonDocument Config { get; set; }         // jsonb NOT NULL
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }

    public Tenant? Account { get; set; }
}
```

### 1.15 ProviderDiagnostic (migration 014)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`

```csharp
public class ProviderDiagnostic
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public string EventType { get; set; }
    public string ProviderName { get; set; }
    public string? Model { get; set; }
    public string? AgentType { get; set; }
    public string? ProjectId { get; set; }
    public string? EngineId { get; set; }
    public string? TaskId { get; set; }
    public string? TaskType { get; set; }
    public int InputTokens { get; set; }             // default 0
    public int OutputTokens { get; set; }            // default 0
    public int LatencyMs { get; set; }               // default 0
    public decimal CostUsd { get; set; }             // numeric(12,6)
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public Guid? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Tenant? Account { get; set; }
}
```

### 1.16 ProviderHealth (migration 015)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderHealth.cs`

```csharp
public class ProviderHealth
{
    public string Key { get; set; }                  // PK, text
    public bool CircuitOpen { get; set; }            // default false
    public DateTimeOffset? CircuitOpenUntil { get; set; }
    public int FailureCount { get; set; }            // default 0
    public DateTimeOffset? LastFailureAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public bool HalfOpenInProgress { get; set; }     // default false
    public DateTimeOffset UpdatedAt { get; set; }
}
```

### 1.17 SanitizationRule (migration 016)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs`

```csharp
public class SanitizationRule
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }             // UNIQUE
    public bool Enabled { get; set; }                // default true
    public string[] ExtraInjectionPatterns { get; set; }  // text[]
    public string[] BlockedCommandPatterns { get; set; }  // text[]
    public int MaxFetchSizeBytes { get; set; }       // default 10485760
    public bool ValidateUrls { get; set; }           // default true
    public bool GateActions { get; set; }            // default true
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Tenant? Account { get; set; }
}
```

### 1.18 TenantMembership (migration 017)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs`

```csharp
public class TenantMembership
{
    public Guid TenantId { get; set; }               // PK part 1
    public Guid UserId { get; set; }                 // PK part 2
    public string Role { get; set; }                 // text, default 'member'
    public DateTimeOffset JoinedAt { get; set; }

    // Navigation
    public Tenant Tenant { get; set; }
    public User User { get; set; }
}
```

### 1.19 TenantInvite (migration 017)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/TenantInvite.cs`

```csharp
public class TenantInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string Email { get; set; }
    public string Role { get; set; }                 // default 'member'
    public string InviteTokenHash { get; set; }      // text UNIQUE NOT NULL
    public Guid InvitedBy { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Tenant Tenant { get; set; }
    public User InvitedByUser { get; set; }
}
```

### 1.20 RefreshToken (migration 018)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/RefreshToken.cs`

```csharp
public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; }            // text UNIQUE NOT NULL
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; }
}
```

### 1.21 PasswordResetToken (migration 018)

**File**: `apps/tamma-elsa/src/Tamma.Data/Entities/PasswordResetToken.cs`

```csharp
public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; }            // text UNIQUE NOT NULL
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; }
}
```

### 1.22 LegacyApiKey — not materialized

Migration 003 only adds columns to `github_installations`. The `api_keys` table (003)
is the legacy name reused in migration 009 as `api_keys` (the unified table). No
separate legacy entity needed.

---

## Task 2: TammaDbContext Rewrite

**File**: `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`

Replace the current 4-entity context with all 21 DbSets. Keep the existing mentorship
entities unchanged (they already have correct table mappings).

### 2.1 DbSets

```csharp
// Existing (keep)
public DbSet<MentorshipSession> MentorshipSessions => Set<MentorshipSession>();
public DbSet<MentorshipEvent> MentorshipEvents => Set<MentorshipEvent>();
public DbSet<JuniorDeveloper> JuniorDevelopers => Set<JuniorDeveloper>();
public DbSet<Story> Stories => Set<Story>();

// New — Phase 1
public DbSet<GitHubInstallation> GitHubInstallations => Set<GitHubInstallation>();
public DbSet<GitHubInstallationRepo> GitHubInstallationRepos => Set<GitHubInstallationRepo>();
public DbSet<User> Users => Set<User>();
public DbSet<UserInstallation> UserInstallations => Set<UserInstallation>();
public DbSet<UserApiKey> UserApiKeys => Set<UserApiKey>();
public DbSet<UserInvite> UserInvites => Set<UserInvite>();
public DbSet<Tenant> Tenants => Set<Tenant>();
public DbSet<UnifiedApiKey> ApiKeys => Set<UnifiedApiKey>();
public DbSet<EngineEvent> EngineEvents => Set<EngineEvent>();
public DbSet<WorkflowInstance> WorkflowInstances => Set<WorkflowInstance>();
public DbSet<Prompt> Prompts => Set<Prompt>();
public DbSet<SystemPrompt> SystemPrompts => Set<SystemPrompt>();
public DbSet<ActionPrompt> ActionPrompts => Set<ActionPrompt>();
public DbSet<AgentConfig> AgentConfigs => Set<AgentConfig>();
public DbSet<ProviderDiagnostic> ProviderDiagnostics => Set<ProviderDiagnostic>();
public DbSet<ProviderHealth> ProviderHealthRecords => Set<ProviderHealth>();
public DbSet<SanitizationRule> SanitizationRules => Set<SanitizationRule>();
public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
public DbSet<TenantInvite> TenantInvites => Set<TenantInvite>();
public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
```

### 2.2 Constructor — inject TenantContext

```csharp
private readonly TenantContext _tenantContext;

public TammaDbContext(DbContextOptions<TammaDbContext> options, TenantContext tenantContext)
    : base(options)
{
    _tenantContext = tenantContext;
}

// Parameterless ctor for EF Core design-time tooling
public TammaDbContext(DbContextOptions<TammaDbContext> options)
    : base(options)
{
    _tenantContext = new TenantContext();
}
```

### 2.3 OnModelCreating — table mappings + global query filters

The method must configure all 21 new entities with:
- `ToTable("snake_case_name")`
- `HasColumnName("snake_case")` for every property
- Composite primary keys (UserInstallation, TenantMembership)
- Indexes matching the SQL CREATE INDEX statements
- JSONB column types for `JsonDocument` properties
- PostgreSQL text array for `string[]` properties (SanitizationRule)
- Check constraints where SQL uses CHECK
- `HasDefaultValueSql("now()")` for timestamp columns
- Navigation/FK relationships

### 2.4 Global Query Filters for Tenant Isolation

Entities with `TenantId` get automatic filtering:

```csharp
// Pattern applied to: User, GitHubInstallation, UserApiKey, UserInvite,
// EngineEvent, WorkflowInstance, UnifiedApiKey (when TenantId is non-null)
builder.Entity<User>().HasQueryFilter(u =>
    _tenantContext.TenantId == null || u.TenantId == _tenantContext.TenantId);
```

Entities where TenantId is nullable (Prompt, SystemPrompt, ActionPrompt, AgentConfig):
- Do NOT apply global query filter — they need cross-tenant reads for system defaults.

### 2.5 Partial Unique Indexes

EF Core 8 supports `HasFilter()` for partial indexes:

```csharp
builder.Entity<Prompt>()
    .HasIndex(p => new { p.Role, p.Action })
    .HasFilter("tenant_id IS NULL")
    .IsUnique()
    .HasDatabaseName("idx_prompts_system_default");
```

---

## Task 3: TenantContext Scoped Service

**File**: `apps/tamma-elsa/src/Tamma.Data/TenantContext.cs`

```csharp
namespace Tamma.Data;

public class TenantContext
{
    public Guid? TenantId { get; set; }
}
```

Registered in DI as scoped: `builder.Services.AddScoped<TenantContext>();`

---

## Task 4: EF Core Initial Migration

### 4.1 Add NuGet package references

**File**: `apps/tamma-elsa/src/Tamma.Data/Tamma.Data.csproj` — add if missing:
- `Microsoft.EntityFrameworkCore` 8.0.0 (already present)
- `Npgsql.EntityFrameworkCore.PostgreSQL` 8.0.0 (already present)
- `Microsoft.EntityFrameworkCore.Design` 8.0.0 (already present)

No new NuGet dependencies needed for Tamma.Data.

### 4.2 Generate migration

```bash
cd apps/tamma-elsa/src/Tamma.Data
dotnet ef migrations add InitialPlatformSchema \
  --startup-project ../Tamma.Api/Tamma.Api.csproj \
  --context TammaDbContext
```

### 4.3 Verify schema equivalence

```bash
# Dump existing schema
pg_dump --schema-only --no-owner --no-privileges tamma > /tmp/schema_before.sql

# Apply EF migration to a fresh DB
dotnet ef database update --startup-project ../Tamma.Api/Tamma.Api.csproj
pg_dump --schema-only --no-owner --no-privileges tamma_ef_test > /tmp/schema_after.sql

# Compare (diff should show only ordering/naming differences, not structural)
diff /tmp/schema_before.sql /tmp/schema_after.sql
```

### 4.4 Archive SQL migrations

```bash
mv database/migrations/ database/migrations-archived/
```

---

## Task 5: Port Auth Infrastructure

### 5.1 Permissions + Role Hierarchy

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`
**Ports**: `packages/api/src/auth/permissions.ts`

Define the `Permission` enum, `Role` enum, role hierarchy, and `HasPermission()` method.
Exact permission keys from TS:
- `dashboard:view`, `workflows:view`, `workflows:manage`, `workflows:delete`
- `users:view`, `users:manage`, `admin:access`, `logs:access`, `elsa:access`
- `settings:view`, `settings:manage`, `apikeys:manage`

Role hierarchy: `member(0) < admin(1) < owner(2)`.

### 5.2 AuthPrincipal Model

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs`
**Ports**: `packages/api/src/auth/principal.ts`

Tagged union as C# record hierarchy:

```csharp
public abstract record AuthPrincipal(string Scope, Guid KeyId);
public record UserPrincipal(Guid KeyId, Guid UserId, string Role, Guid TenantId)
    : AuthPrincipal("user", KeyId);
public record InstallationPrincipal(Guid KeyId, int InstallationId, Guid TenantId)
    : AuthPrincipal("installation", KeyId);
public record ServicePrincipal(Guid KeyId, string ServiceName, string[] Permissions, Guid? TenantId)
    : AuthPrincipal("service", KeyId);
```

### 5.3 PermissionRequirement + Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionRequirement.cs`
**Ports**: `packages/api/src/auth/require-permission.ts`

ASP.NET Core `IAuthorizationRequirement` + `AuthorizationHandler`:
- Extract role from `ClaimsPrincipal` or `AuthPrincipal` in HttpContext.Items
- Check `HasPermission(role, requiredPermission)`
- Return 403 if insufficient

### 5.4 ScopeRequirement + Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/ScopeRequirement.cs`
**Ports**: `packages/api/src/auth/require-scope.ts`

Only enforces scope checks on service principals. User and installation principals
are authorized by RBAC elsewhere.

### 5.5 RoleRequirement + Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/RoleRequirement.cs`
**Ports**: `packages/api/src/middleware/require-role.ts`

Checks minimum role level using the hierarchy. Supports self-or-role pattern.

### 5.6 TenantRequirement + Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/TenantRequirement.cs`
**Ports**: `packages/api/src/middleware/require-tenant.ts`

Verifies user membership in the active tenant from JWT claims.

### 5.7 TenantRoleRequirement + Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Auth/TenantRoleRequirement.cs`
**Ports**: `packages/api/src/middleware/require-tenant-role.ts`

Checks minimum role within the active tenant. Must run after TenantRequirement.

### 5.8 API Key Service

**File**: `apps/tamma-elsa/src/Tamma.Api/Services/ApiKeyService.cs`
**Ports**: `packages/api/src/auth/api-key.ts`

Methods:
- `GenerateApiKey()` — returns `tamma_sk_` + 32 random bytes base64url
- `HashApiKey(key)` — scrypt with fixed salt (N=16384, r=8, p=1, keylen=32)
- `GetApiKeyPrefix(key)` — first 12 characters for display

Use `System.Security.Cryptography.Rfc2898DeriveBytes` or the
`Konscious.Security.Cryptography` NuGet for scrypt. Since the TS implementation uses
Node.js `scryptSync`, the C# version must produce identical hashes. Add a cross-
language test with known input/output pairs.

**NuGet dependency**: `Konscious.Security.Cryptography.SCrypt` (for scrypt hash compat).

### 5.9 API Key Auth Handler

**File**: `apps/tamma-elsa/src/Tamma.Api/Middleware/ApiKeyAuthHandler.cs`
**Ports**: `packages/api/src/auth/api-key-auth.ts` + `packages/api/src/auth/unified-auth.ts`

Custom `AuthenticationHandler<AuthenticationSchemeOptions>`:
1. Extract `Authorization: Bearer tamma_sk_...` header
2. Hash key, look up in `api_keys` table via repository
3. Check revocation and grace period
4. Build `AuthPrincipal` based on scope (user/installation/service)
5. Set `HttpContext.Items["AuthPrincipal"]` for downstream use
6. Fire-and-forget `UpdateLastUsed` via `IServiceScopeFactory`

### 5.10 Unified Auth Setup (JWT + API Key)

**File**: `apps/tamma-elsa/src/Tamma.Api/Middleware/AuthenticationSetup.cs`
**Ports**: `packages/api/src/auth/index.ts`

Extension method on `IServiceCollection`:

```csharp
public static IServiceCollection AddTammaAuth(
    this IServiceCollection services,
    IConfiguration config)
{
    services.AddAuthentication(options =>
    {
        options.DefaultScheme = "TammaUnified";
        options.DefaultChallengeScheme = "TammaUnified";
    })
    .AddJwtBearer("JwtBearer", options => { /* JWT config */ })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null)
    .AddPolicyScheme("TammaUnified", "JWT or API Key", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var auth = context.Request.Headers.Authorization.ToString();
            return auth.StartsWith("Bearer tamma_sk_") ? "ApiKey" : "JwtBearer";
        };
    });
}
```

### 5.11 Password Service

**File**: `apps/tamma-elsa/src/Tamma.Api/Services/PasswordService.cs`
**Ports**: `packages/api/src/auth/password.ts`

Methods:
- `ValidatePasswordStrength(password)` — same rules: 8-128 chars, upper+lower+digit, common list
- `HashPassword(password)` — scrypt with random salt, format: `scrypt:N:r:p:keylen:salt:hash`
- `VerifyPassword(password, storedHash)` — parse format, constant-time compare

Must produce hashes compatible with the TS implementation (same scrypt parameters:
N=16384, r=8, p=1, keylen=32, salt=16 bytes). Existing password hashes from the TS
API must verify correctly in the C# service.

### 5.12 Login Lockout Service

**File**: `apps/tamma-elsa/src/Tamma.Api/Services/LoginLockoutService.cs`
**Ports**: `packages/api/src/auth/login-lockout.ts`

In-memory implementation with `ConcurrentDictionary`:
- 5 failed attempts in 15 minutes -> 30-minute lockout
- `RecordFailedAttempt(email)` -> returns bool (locked)
- `IsLocked(email)` -> bool
- `ResetAttempts(email)` -> void
- `GetRemainingLockoutSeconds(email)` -> int

Interface: `ILoginLockoutService` for DI and testing.

---

## Task 6: Port Tenant Context Middleware

### 6.1 TenantContextMiddleware

**File**: `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`
**Ports**: `packages/api/src/middleware/tenant-context.ts`

ASP.NET Core middleware that runs after authentication. Resolution priority:
1. AuthPrincipal.TenantId (from API key auth)
2. JWT claim `tenantId` (from OAuth/dashboard)
3. Installation context -> tenant lookup
4. User -> user.TenantId from DB
5. Auth disabled (dev mode) -> DEFAULT_TENANT_ID (`00000000-0000-0000-0000-000000000000`)

Tenant-free paths (skip resolution):
`/api/health`, `/api/auth/login`, `/api/auth/api-key`, `/api/auth/callback`, `/api/auth/github`

Sets `TenantContext.TenantId` (the scoped service injected into DbContext).

### 6.2 EnsurePersonalTenantMiddleware

**File**: `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`
**Ports**: `packages/api/src/middleware/ensure-personal-tenant.ts`

Runs after TenantContextMiddleware. If the user has no tenantId:
1. Check existing memberships -> pick most recent
2. No memberships -> auto-create personal tenant with slug `u-{userId.Substring(0,8)}`
3. Add user as owner, update `users.tenant_id`

---

## Task 7: Middleware Pipeline Order

**File**: `apps/tamma-elsa/src/Tamma.Api/Program.cs`

Update the pipeline configuration. The exact order matters:

```csharp
var app = builder.Build();

// 1. Exception handling
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 2. Request logging
app.UseSerilogRequestLogging();

// 3. HTTPS redirection
app.UseHttpsRedirection();

// 4. CORS
app.UseCors("AllowDashboard");

// 5. Authentication (JWT + API Key via policy scheme)
app.UseAuthentication();

// 6. Authorization (RBAC)
app.UseAuthorization();

// 7. Tenant resolution (sets TenantContext.TenantId)
app.UseMiddleware<TenantContextMiddleware>();

// 8. Auto-provision personal tenant
app.UseMiddleware<EnsurePersonalTenantMiddleware>();

// 9. Route handlers
app.MapControllers();
app.MapHealthChecks("/health");
```

### 7.1 DI Registration in Program.cs

```csharp
// Scoped services
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ILoginLockoutService, LoginLockoutService>();
builder.Services.AddScoped<IApiKeyService, ApiKeyService>();
builder.Services.AddScoped<IPasswordService, PasswordService>();

// Auth setup
builder.Services.AddTammaAuth(builder.Configuration);

// Authorization policies
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminAccess", policy =>
        policy.AddRequirements(new PermissionRequirement("admin:access")));
    options.AddPolicy("UsersView", policy =>
        policy.AddRequirements(new PermissionRequirement("users:view")));
    // ... one policy per permission key
});
builder.Services.AddSingleton<IAuthorizationHandler, PermissionHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, ScopeHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, RoleHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, TenantHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, TenantRoleHandler>();
```

---

## Task 8: Repository Interfaces

Phase 1 creates interfaces only. Implementations come alongside route porting in Phases
2-4. However, the repositories needed by auth middleware are implemented now.

### 8.1 Interfaces (all in `Tamma.Data/Repositories/`)

| File | Interface | Methods |
|---|---|---|
| `IUnifiedApiKeyRepository.cs` | `IUnifiedApiKeyRepository` | `FindByKeyHash`, `UpdateLastUsed`, `Create`, `ListByOwner`, `Rotate`, `Revoke` |
| `ITenantRepository.cs` | `ITenantRepository` | `GetById`, `GetBySlug`, `GetByExternalId`, `Create`, `Update`, `Delete` |
| `IUserRepository.cs` | `IUserRepository` | `GetById`, `GetByEmail`, `GetByGitHubId`, `UpdateActiveTenant`, `List`, `SoftDelete` |
| `ITenantMembershipRepository.cs` | `ITenantMembershipRepository` | `GetMembership`, `AddMember`, `RemoveMember`, `ListByTenant`, `GetUserTenants` |

### 8.2 EF Core Implementations (auth-critical only)

| File | Class |
|---|---|
| `Tamma.Data/Repositories/UnifiedApiKeyRepository.cs` | `UnifiedApiKeyRepository` |
| `Tamma.Data/Repositories/TenantRepository.cs` | `TenantRepository` |
| `Tamma.Data/Repositories/UserRepository.cs` | `UserRepository` |
| `Tamma.Data/Repositories/TenantMembershipRepository.cs` | `TenantMembershipRepository` |

Each uses `TammaDbContext` via constructor injection. Global query filters handle
tenant isolation automatically.

---

## Task 9: NuGet Dependencies

### Tamma.Api.csproj — add:

```xml
<PackageReference Include="Konscious.Security.Cryptography.SCrypt" Version="1.3.0" />
<PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="8.0.0" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="7.0.0" />
```

### Tamma.Api.Tests.csproj — add:

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="8.0.0" />
```

---

## Task 10: Tests

All tests use xUnit (existing test project uses NUnit — evaluate migrating to xUnit for
consistency with the story spec, or keep NUnit if team prefers). The test project already
has `Microsoft.AspNetCore.Mvc.Testing` for integration tests.

### 10.1 Entity Configuration Tests (30 tests)

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Data/EntityConfigurationTests.cs`

Tests:
- Each entity maps to the correct table name
- Column names are snake_case
- Primary keys are correct (composite for UserInstallation, TenantMembership)
- Required/nullable columns match SQL schema
- JSONB columns use `HasColumnType("jsonb")`
- Default values match SQL defaults
- Indexes exist and match SQL index names
- Unique constraints on key_hash, slug, email columns
- Partial unique indexes (prompts system_default, agent_configs)
- Foreign key cascades match SQL ON DELETE behavior

### 10.2 Global Query Filter Tests (10 tests)

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Data/QueryFilterTests.cs`

Tests using InMemory provider:
- Tenant-scoped entity returns only matching tenant rows
- Null TenantContext returns all rows (admin bypass)
- Cross-tenant query returns empty when filter is active
- Entities without TenantId (ProviderHealth) return all rows regardless
- Prompt/AgentConfig (nullable TenantId) return both system defaults and tenant overrides

### 10.3 Auth Middleware Tests (15 tests)

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/ApiKeyAuthHandlerTests.cs`

Tests:
- Valid API key returns 200 with correct AuthPrincipal
- Missing Authorization header returns 401
- Invalid key hash returns 401
- Revoked key (past grace period) returns 401
- Key in grace period succeeds with warning log
- User-scope key populates UserPrincipal with correct role
- Installation-scope key populates InstallationPrincipal
- Service-scope key reads X-Tenant-Id header
- Service-scope key with invalid X-Tenant-Id returns 400

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionTests.cs`

Tests:
- Owner has all permissions
- Admin has admin-level permissions but not owner-only
- Member has member-level permissions only
- Unknown role returns false
- `GetRolePermissions` returns correct set for each role
- `IsValidRole` validates correctly

### 10.4 Tenant Context Middleware Tests (10 tests)

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Middleware/TenantContextMiddlewareTests.cs`

Tests:
- Tenant-free path skips resolution
- API key principal tenantId is used (priority 1)
- JWT claim tenantId is used when no principal (priority 2)
- User DB lookup fallback works (priority 4)
- Dev mode uses DEFAULT_TENANT_ID
- Missing tenant returns 403
- TenantContext.TenantId is set after middleware

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Middleware/EnsurePersonalTenantMiddlewareTests.cs`

Tests:
- User with existing tenantId is no-op
- User with existing membership gets tenant set
- User with no membership gets personal tenant auto-created
- Slug collision retries with suffix

### 10.5 Password + Lockout Tests

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/PasswordServiceTests.cs`

Tests:
- Hash + verify round-trip succeeds
- Verify with wrong password fails
- Verify with TS-generated hash succeeds (cross-language compat)
- Validate password strength: too short, no uppercase, common password

**File**: `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/LoginLockoutServiceTests.cs`

Tests:
- 5 failures lock the account
- Locked account returns true for IsLocked
- Successful login resets counter
- Lockout expires after configured duration
- GetRemainingLockoutSeconds returns correct value

---

## Test Commands

```bash
# Run all Phase 1 tests
cd apps/tamma-elsa
dotnet test tests/Tamma.Api.Tests/ --filter "FullyQualifiedName~Data.|FullyQualifiedName~Auth.|FullyQualifiedName~Middleware.|FullyQualifiedName~Services.Password|FullyQualifiedName~Services.LoginLockout"

# Run entity configuration tests only
dotnet test tests/Tamma.Api.Tests/ --filter "FullyQualifiedName~Data.EntityConfigurationTests"

# Run query filter tests only
dotnet test tests/Tamma.Api.Tests/ --filter "FullyQualifiedName~Data.QueryFilterTests"

# Run auth tests only
dotnet test tests/Tamma.Api.Tests/ --filter "FullyQualifiedName~Auth."

# Run middleware tests only
dotnet test tests/Tamma.Api.Tests/ --filter "FullyQualifiedName~Middleware."

# Run all tests with coverage
dotnet test tests/Tamma.Api.Tests/ --collect:"XPlat Code Coverage"
```

---

## File Summary

### New files (34 total)

**Entities (21 files)**:
- `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallation.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/GitHubInstallationRepo.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/User.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/UserInstallation.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/UserApiKey.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/Tenant.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/UnifiedApiKey.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/EngineEvent.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/WorkflowInstance.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/Prompt.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/SystemPrompt.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/ActionPrompt.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/AgentConfig.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderDiagnostic.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/ProviderHealth.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/SanitizationRule.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/TenantMembership.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/TenantInvite.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/RefreshToken.cs`
- `apps/tamma-elsa/src/Tamma.Data/Entities/PasswordResetToken.cs`

**Auth (7 files)**:
- `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/AuthPrincipal.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionRequirement.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/ScopeRequirement.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/RoleRequirement.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/TenantRequirement.cs`
- `apps/tamma-elsa/src/Tamma.Api/Auth/TenantRoleRequirement.cs`

**Middleware (3 files)**:
- `apps/tamma-elsa/src/Tamma.Api/Middleware/AuthenticationSetup.cs`
- `apps/tamma-elsa/src/Tamma.Api/Middleware/TenantContextMiddleware.cs`
- `apps/tamma-elsa/src/Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`

**Services (3 files)**:
- `apps/tamma-elsa/src/Tamma.Api/Services/ApiKeyService.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/PasswordService.cs`
- `apps/tamma-elsa/src/Tamma.Api/Services/LoginLockoutService.cs`

**Data (5 files)**:
- `apps/tamma-elsa/src/Tamma.Data/TenantContext.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IUnifiedApiKeyRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IUserRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/ITenantMembershipRepository.cs`

**Repository implementations (4 files)**:
- `apps/tamma-elsa/src/Tamma.Data/Repositories/UnifiedApiKeyRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/UserRepository.cs`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantMembershipRepository.cs`

### Modified files (3)

- `apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs` — rewrite with 21 new DbSets
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — new middleware pipeline + DI
- `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj` — new NuGet refs

### Test files (7)

- `apps/tamma-elsa/tests/Tamma.Api.Tests/Data/EntityConfigurationTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Data/QueryFilterTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/ApiKeyAuthHandlerTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Auth/PermissionTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Middleware/TenantContextMiddlewareTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Middleware/EnsurePersonalTenantMiddlewareTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/PasswordServiceTests.cs`
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Services/LoginLockoutServiceTests.cs`

### Archived (1 directory)

- `database/migrations/` -> `database/migrations-archived/`

---

## Migration Strategy

1. **No breaking changes**: Both APIs continue running. Nginx routing is unchanged.
2. **Schema compatibility**: The EF Core migration must produce a schema that matches
   the existing DB exactly. Run `pg_dump` comparison to verify.
3. **Password hash compatibility**: The C# `PasswordService` must verify hashes created
   by the TS `password.ts` (same scrypt parameters). Add a cross-language test with a
   known password + hash pair from the TS tests.
4. **API key hash compatibility**: Same requirement — `ApiKeyService.HashApiKey()` must
   produce identical output to the TS `hashApiKey()` for the same input.
5. **JWT compatibility**: Both APIs share the same JWT secret. Tokens issued by the TS
   API must be valid in the C# API and vice versa. The JWT payload structure
   (`UnifiedJwtPayload`) is identical.

---

## Success Criteria

- [ ] All 21 entities mapped with correct column types, indexes, and constraints
- [ ] `dotnet ef migrations add` generates schema matching `pg_dump` of existing DB
- [ ] Global query filters verified: cross-tenant queries return empty results
- [ ] JWT auth + API key auth passing in integration tests
- [ ] Password hash cross-compatibility verified (TS hash verifies in C#)
- [ ] API key hash cross-compatibility verified
- [ ] All 55 Phase 1 tests green
- [ ] `dotnet test` passes with zero failures
- [ ] Existing mentorship entities and tests unaffected
