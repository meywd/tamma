# Epic 19 Phase 1 Implementation Plan -- Greenfield C# API Build

Target: `apps/tamma-elsa/src/Tamma.Api/` (expand existing project)
Database: `apps/tamma-elsa/src/Tamma.Data/` (expand existing DbContext)
Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/` (expand existing test project)

All paths below are relative to `apps/tamma-elsa/src/` unless otherwise noted.

---

## Task 1: EF Core DbContext + Entities

### 1.1 Create Entity Classes

Create all entities in `Tamma.Data/Entities/`. Each entity is a plain C# class
with navigation properties. Column mapping and indexes go in `OnModelCreating`.

**File**: `Tamma.Data/Entities/User.cs`

```csharp
namespace Tamma.Data.Entities;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = null!;
    public string? PasswordHash { get; set; }
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "member"; // member | admin | owner
    public Guid? TenantId { get; set; }
    public bool EmailVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public string AuthMethod { get; set; } = "email"; // email | github | both
    public int? GitHubId { get; set; }
    public string? GitHubLogin { get; set; }
    public string? EmailVerificationTokenHash { get; set; }
    public DateTime? EmailVerificationExpiresAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; } // soft delete

    // Navigation
    public Tenant? Tenant { get; set; }
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
    public ICollection<PasswordResetToken> PasswordResetTokens { get; set; } = [];
    public ICollection<ApiKey> ApiKeys { get; set; } = [];
}
```

**File**: `Tamma.Data/Entities/RefreshToken.cs`

```csharp
namespace Tamma.Data.Entities;

public class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!; // SHA-256
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/PasswordResetToken.cs`

```csharp
namespace Tamma.Data.Entities;

public class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = null!; // SHA-256
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public User User { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/Tenant.cs`

```csharp
namespace Tamma.Data.Entities;

public class Tenant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Type { get; set; } = "personal"; // personal | org
    public Guid? OwnerId { get; set; }
    public string? ExternalId { get; set; }
    public string Plan { get; set; } = "free";
    public string Settings { get; set; } = "{}"; // JSONB
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public User? Owner { get; set; }
    public ICollection<TenantMembership> Memberships { get; set; } = [];
    public ICollection<UserInvite> Invites { get; set; } = [];
}
```

**File**: `Tamma.Data/Entities/TenantMembership.cs`

```csharp
namespace Tamma.Data.Entities;

public class TenantMembership
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "member";
    public DateTime JoinedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
    public User User { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/UserInvite.cs`

```csharp
namespace Tamma.Data.Entities;

public class UserInvite
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string? Email { get; set; }
    public string Role { get; set; } = "member";
    public string InviteTokenHash { get; set; } = null!;
    public Guid InvitedBy { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    public Tenant Tenant { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/ApiKey.cs`

```csharp
namespace Tamma.Data.Entities;

public class ApiKey
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = null!; // user | installation | service
    public string OwnerId { get; set; } = null!; // user GUID or installation ID
    public string KeyHash { get; set; } = null!; // Argon2id
    public string KeyPrefix { get; set; } = null!; // first 8 chars for identification
    public string Label { get; set; } = null!;
    public string[] Permissions { get; set; } = [];
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RotatedFromId { get; set; }
}
```

**File**: `Tamma.Data/Entities/GitHubInstallation.cs`

```csharp
namespace Tamma.Data.Entities;

public class GitHubInstallation
{
    public Guid Id { get; set; }
    public long InstallationId { get; set; } // GitHub's numeric ID
    public string AccountLogin { get; set; } = null!;
    public string AccountType { get; set; } = null!; // User | Organization
    public int AppId { get; set; }
    public string? AppSlug { get; set; }
    public string Permissions { get; set; } = "{}"; // JSONB
    public DateTime? SuspendedAt { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<GitHubInstallationRepo> Repos { get; set; } = [];
}
```

**File**: `Tamma.Data/Entities/GitHubInstallationRepo.cs`

```csharp
namespace Tamma.Data.Entities;

public class GitHubInstallationRepo
{
    public Guid Id { get; set; }
    public Guid InstallationEntityId { get; set; }
    public long RepoId { get; set; } // GitHub's numeric repo ID
    public string RepoFullName { get; set; } = null!; // owner/name
    public bool IsActive { get; set; } = true;

    public GitHubInstallation Installation { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/AgentConfig.cs`

```csharp
namespace Tamma.Data.Entities;

public class AgentConfig
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; } // null = system default
    public string Config { get; set; } = "{}"; // JSONB: { agents, security }
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

**File**: `Tamma.Data/Entities/PromptOverride.cs`

```csharp
namespace Tamma.Data.Entities;

public class PromptOverride
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Scope { get; set; } = null!; // role-system | action-default | role-action
    public string? Role { get; set; }
    public string? Action { get; set; }
    public string Template { get; set; } = null!;
    public string? SystemPrompt { get; set; }
    public string[] Variables { get; set; } = [];
    public bool EnableTools { get; set; }
    public int MaxTokens { get; set; } = 4096;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**File**: `Tamma.Data/Entities/ProviderHealth.cs`

```csharp
namespace Tamma.Data.Entities;

public class ProviderHealth
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;
    public string Status { get; set; } = "unknown"; // healthy | degraded | down | unknown
    public DateTime? LastSuccess { get; set; }
    public DateTime? LastFailure { get; set; }
    public int FailureCount { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**File**: `Tamma.Data/Entities/ProviderDiagnostic.cs`

```csharp
namespace Tamma.Data.Entities;

public class ProviderDiagnostic
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;
    public double RequestDurationMs { get; set; }
    public int TokensUsed { get; set; }
    public decimal Cost { get; set; }
    public Guid? TenantId { get; set; }
    public string? Model { get; set; }
    public string? RequestType { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

**File**: `Tamma.Data/Entities/SanitizationRule.cs`

```csharp
namespace Tamma.Data.Entities;

public class SanitizationRule
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }
    public string Rules { get; set; } = "{}"; // JSONB
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

**File**: `Tamma.Data/Entities/WorkflowDefinition.cs`

```csharp
namespace Tamma.Data.Entities;

public class WorkflowDefinition
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public string Steps { get; set; } = "[]"; // JSONB
    public Guid? TenantId { get; set; }
    public DateTime SyncedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<WorkflowInstance> Instances { get; set; } = [];
}
```

**File**: `Tamma.Data/Entities/WorkflowInstance.cs`

```csharp
namespace Tamma.Data.Entities;

public class WorkflowInstance
{
    public Guid Id { get; set; }
    public Guid DefinitionId { get; set; }
    public Guid? TenantId { get; set; }
    public string Status { get; set; } = "pending";
    public string? CurrentActivity { get; set; }
    public string Variables { get; set; } = "{}"; // JSONB
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public WorkflowDefinition Definition { get; set; } = null!;
}
```

**File**: `Tamma.Data/Entities/DomainEvent.cs`

```csharp
namespace Tamma.Data.Entities;

public class DomainEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = null!;
    public Guid? TenantId { get; set; }
    public int? IssueNumber { get; set; }
    public string Tags { get; set; } = "{}";     // JSONB
    public string Metadata { get; set; } = "{}";  // JSONB
    public string Data { get; set; } = "{}";      // JSONB
    public DateTime CreatedAt { get; set; }
}
```

**Total**: 18 new entities + 4 existing mentorship entities = 22 entities.

### 1.2 Rewrite TammaDbContext

**File**: `Tamma.Data/TammaDbContext.cs`

Rewrite from scratch. Keep the existing mentorship entity configurations.
Add all 18 new DbSets.

Key responsibilities:
- Inject `ITenantContext` via constructor for global query filters
- Register all DbSets
- Configure `OnModelCreating` with Fluent API:
  - snake_case table names (`ToTable("users")`)
  - JSONB columns via `.HasColumnType("jsonb")`
  - Composite unique indexes (e.g., `TenantMembership(TenantId, UserId)`)
  - Soft delete filters (`.HasQueryFilter(u => u.DeletedAt == null)`)
  - Tenant isolation filters (see Task 1.3)
  - UUID primary keys with `HasDefaultValueSql("gen_random_uuid()")`
  - Timestamp defaults with `HasDefaultValueSql("now()")`
  - String array columns via `.HasColumnType("text[]")`

**Global query filters for tenant isolation** (all tenant-scoped entities):

```csharp
public class TammaDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public TammaDbContext(DbContextOptions<TammaDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Tenant-scoped entities get automatic filtering
        modelBuilder.Entity<User>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<AgentConfig>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<PromptOverride>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<WorkflowDefinition>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<WorkflowInstance>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<DomainEvent>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<ProviderHealth>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<ProviderDiagnostic>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        modelBuilder.Entity<SanitizationRule>().HasQueryFilter(e =>
            _tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId);

        // Soft-delete filter on User
        modelBuilder.Entity<User>().HasQueryFilter(e =>
            e.DeletedAt == null &&
            (_tenantContext.TenantId == null || e.TenantId == _tenantContext.TenantId));

        // Soft-delete filter on Tenant
        modelBuilder.Entity<Tenant>().HasQueryFilter(e => e.DeletedAt == null);

        // ... (all Fluent API configuration)
    }
}
```

**Key indexes to configure**:

| Entity | Index | Type |
|---|---|---|
| User | Email | Unique (where DeletedAt IS NULL) |
| User | GitHubId | Unique (where non-null, DeletedAt IS NULL) |
| User | TenantId | Regular |
| Tenant | Slug | Unique (where DeletedAt IS NULL) |
| Tenant | ExternalId | Unique (where non-null, DeletedAt IS NULL) |
| TenantMembership | (TenantId, UserId) | Unique composite |
| UserInvite | InviteTokenHash | Unique |
| UserInvite | TenantId | Regular |
| ApiKey | KeyHash | Unique |
| ApiKey | (Scope, OwnerId) | Regular |
| ApiKey | TenantId | Regular |
| GitHubInstallation | InstallationId | Unique |
| GitHubInstallationRepo | (InstallationEntityId, RepoId) | Unique composite |
| RefreshToken | TokenHash | Unique |
| RefreshToken | UserId | Regular |
| PasswordResetToken | TokenHash | Unique |
| PasswordResetToken | UserId | Regular |
| AgentConfig | TenantId | Unique (nullable) |
| PromptOverride | (UserId, Scope, Role, Action) | Unique composite |
| ProviderHealth | (ProviderKey, TenantId) | Unique composite |
| ProviderDiagnostic | (ProviderKey, CreatedAt) | Regular |
| DomainEvent | (Type, CreatedAt) | Regular |
| DomainEvent | TenantId | Regular |
| WorkflowDefinition | TenantId | Regular |
| WorkflowInstance | (DefinitionId, Status) | Regular |
| WorkflowInstance | TenantId | Regular |

### 1.3 ITenantContext Service

**File**: `Tamma.Data/ITenantContext.cs`

```csharp
namespace Tamma.Data;

public interface ITenantContext
{
    Guid? TenantId { get; }
    void SetTenantId(Guid tenantId);
    void ClearTenantId(); // admin bypass
}
```

**File**: `Tamma.Data/TenantContext.cs`

```csharp
namespace Tamma.Data;

public class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }

    public void SetTenantId(Guid tenantId) => TenantId = tenantId;
    public void ClearTenantId() => TenantId = null;
}
```

Register as `Scoped` in DI. The middleware sets it per-request.

### 1.4 Generate Initial Migration

```bash
cd apps/tamma-elsa/src
dotnet ef migrations add InitialCreate \
    --project Tamma.Data \
    --startup-project Tamma.Api \
    --output-dir Migrations
```

Verify the generated SQL creates all tables with correct column types, indexes,
and JSONB columns. Commit the migration.

### 1.5 NuGet Packages for Tamma.Data

Add to `Tamma.Data/Tamma.Data.csproj`:

```xml
<!-- Already present -->
<PackageReference Include="Microsoft.EntityFrameworkCore" Version="8.0.0" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="8.0.0" />
```

No new packages needed for Tamma.Data.

---

## Task 2: Repository Layer

Create one repository per aggregate root in `Tamma.Data/Repositories/`.
Each injects `TammaDbContext` and uses LINQ queries.

### 2.1 Repository Interfaces

**File**: `Tamma.Data/Repositories/IUserRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<User> CreateAsync(User user)` |
| GetByIdAsync | `Task<User?> GetByIdAsync(Guid id)` |
| GetByEmailAsync | `Task<User?> GetByEmailAsync(string email)` |
| GetByGitHubIdAsync | `Task<User?> GetByGitHubIdAsync(int githubId)` |
| ListAsync | `Task<(List<User> Users, int Total)> ListAsync(int limit, int offset, string? role)` |
| UpdateAsync | `Task<User> UpdateAsync(User user)` |
| SoftDeleteAsync | `Task SoftDeleteAsync(Guid id)` |
| UpdateActiveTenantAsync | `Task UpdateActiveTenantAsync(Guid userId, Guid tenantId)` |

**File**: `Tamma.Data/Repositories/IRefreshTokenRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<RefreshToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt)` |
| GetByTokenHashAsync | `Task<RefreshToken?> GetByTokenHashAsync(string tokenHash)` |
| RevokeAsync | `Task RevokeAsync(Guid id)` |
| RevokeAllForUserAsync | `Task RevokeAllForUserAsync(Guid userId)` |
| CleanExpiredAsync | `Task<int> CleanExpiredAsync()` |

**File**: `Tamma.Data/Repositories/IPasswordResetRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<PasswordResetToken> CreateAsync(Guid userId, string tokenHash, DateTime expiresAt)` |
| GetByTokenHashAsync | `Task<PasswordResetToken?> GetByTokenHashAsync(string tokenHash)` |
| ConsumeAsync | `Task ConsumeAsync(Guid id)` |
| CleanExpiredAsync | `Task<int> CleanExpiredAsync()` |

**File**: `Tamma.Data/Repositories/ITenantRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<Tenant> CreateAsync(Tenant tenant)` |
| GetByIdAsync | `Task<Tenant?> GetByIdAsync(Guid id)` |
| GetBySlugAsync | `Task<Tenant?> GetBySlugAsync(string slug)` |
| GetByExternalIdAsync | `Task<Tenant?> GetByExternalIdAsync(string externalId)` |
| UpdateAsync | `Task<Tenant> UpdateAsync(Tenant tenant)` |
| SoftDeleteAsync | `Task SoftDeleteAsync(Guid id)` |
| ListByUserAsync | `Task<List<Tenant>> ListByUserAsync(Guid userId)` |

**File**: `Tamma.Data/Repositories/ITenantMembershipRepository.cs`

| Method | Signature |
|---|---|
| AddAsync | `Task<TenantMembership> AddAsync(Guid tenantId, Guid userId, string role)` |
| RemoveAsync | `Task RemoveAsync(Guid tenantId, Guid userId)` |
| GetRoleAsync | `Task<string?> GetRoleAsync(Guid tenantId, Guid userId)` |
| ListByTenantAsync | `Task<(List<TenantMembership> Members, int Total)> ListByTenantAsync(Guid tenantId, int limit, int offset)` |
| GetUserTenantsAsync | `Task<List<TenantMembership>> GetUserTenantsAsync(Guid userId)` |
| UpdateRoleAsync | `Task UpdateRoleAsync(Guid tenantId, Guid userId, string role)` |

**File**: `Tamma.Data/Repositories/IInviteRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<UserInvite> CreateAsync(UserInvite invite)` |
| GetByTokenHashAsync | `Task<UserInvite?> GetByTokenHashAsync(string tokenHash)` |
| AcceptAsync | `Task AcceptAsync(Guid id)` |
| ListPendingByTenantAsync | `Task<List<UserInvite>> ListPendingByTenantAsync(Guid tenantId)` |
| DeleteAsync | `Task DeleteAsync(Guid id)` |

**File**: `Tamma.Data/Repositories/IApiKeyRepository.cs`

| Method | Signature |
|---|---|
| CreateAsync | `Task<ApiKey> CreateAsync(ApiKey apiKey)` |
| GetByHashAsync | `Task<ApiKey?> GetByHashAsync(string keyHash)` |
| ListByScopeAsync | `Task<List<ApiKey>> ListByScopeAsync(string scope)` |
| ListByOwnerAsync | `Task<List<ApiKey>> ListByOwnerAsync(string ownerId)` |
| RevokeAsync | `Task RevokeAsync(Guid id)` |
| RotateAsync | `Task<ApiKey> RotateAsync(Guid oldId, string newKeyHash, string newKeyPrefix)` |
| UpdateLastUsedAsync | `Task UpdateLastUsedAsync(Guid id)` |

**File**: `Tamma.Data/Repositories/IInstallationRepository.cs`

| Method | Signature |
|---|---|
| UpsertAsync | `Task<GitHubInstallation> UpsertAsync(GitHubInstallation installation)` |
| GetByInstallationIdAsync | `Task<GitHubInstallation?> GetByInstallationIdAsync(long installationId)` |
| ListAsync | `Task<List<GitHubInstallation>> ListAsync()` |
| ListActiveAsync | `Task<List<GitHubInstallation>> ListActiveAsync()` |
| DeleteAsync | `Task DeleteAsync(long installationId)` |
| SetReposAsync | `Task SetReposAsync(Guid installationEntityId, List<GitHubInstallationRepo> repos)` |
| ListReposAsync | `Task<List<GitHubInstallationRepo>> ListReposAsync(Guid installationEntityId)` |
| SuspendAsync | `Task SuspendAsync(long installationId)` |
| UnsuspendAsync | `Task UnsuspendAsync(long installationId)` |

**File**: `Tamma.Data/Repositories/IAgentConfigRepository.cs`

| Method | Signature |
|---|---|
| GetAsync | `Task<AgentConfig?> GetAsync(Guid? tenantId)` |
| UpsertAsync | `Task<AgentConfig> UpsertAsync(Guid? tenantId, string configJson, Guid? userId)` |
| DeleteAsync | `Task<bool> DeleteAsync(Guid tenantId)` |
| ResolveAsync | `Task<(AgentConfig Config, string Source)> ResolveAsync(Guid tenantId)` |

**File**: `Tamma.Data/Repositories/IPromptRepository.cs`

| Method | Signature |
|---|---|
| GetAsync | `Task<PromptOverride?> GetAsync(Guid? userId, string scope, string? role, string? action)` |
| UpsertAsync | `Task<PromptOverride> UpsertAsync(PromptOverride prompt)` |
| DeleteAsync | `Task<bool> DeleteAsync(Guid? userId, string scope, string? role, string? action)` |
| ListAsync | `Task<List<PromptOverride>> ListAsync(Guid? userId)` |

**File**: `Tamma.Data/Repositories/IProviderHealthRepository.cs`

| Method | Signature |
|---|---|
| RecordSuccessAsync | `Task RecordSuccessAsync(string providerKey, Guid? tenantId)` |
| RecordFailureAsync | `Task RecordFailureAsync(string providerKey, Guid? tenantId)` |
| GetStatusAsync | `Task<ProviderHealth?> GetStatusAsync(string providerKey, Guid? tenantId)` |
| GetAllAsync | `Task<List<ProviderHealth>> GetAllAsync(Guid? tenantId)` |
| ResetAsync | `Task ResetAsync(string providerKey, Guid? tenantId)` |

**File**: `Tamma.Data/Repositories/IDiagnosticsRepository.cs`

| Method | Signature |
|---|---|
| InsertAsync | `Task<Guid> InsertAsync(ProviderDiagnostic diagnostic)` |
| QueryAsync | `Task<(List<ProviderDiagnostic> Items, int Total)> QueryAsync(string? providerKey, DateTime? from, DateTime? to, int limit, int offset)` |
| GetReportAsync | `Task<List<object>> GetReportAsync(DateTime from, DateTime to)` |
| GetBudgetAsync | `Task<object> GetBudgetAsync(string accountId)` |

**File**: `Tamma.Data/Repositories/ISanitizationRepository.cs`

| Method | Signature |
|---|---|
| GetRulesAsync | `Task<SanitizationRule?> GetRulesAsync(Guid? tenantId)` |
| UpsertRulesAsync | `Task<SanitizationRule> UpsertRulesAsync(Guid? tenantId, string rulesJson)` |

**File**: `Tamma.Data/Repositories/IWorkflowRepository.cs`

| Method | Signature |
|---|---|
| UpsertDefinitionAsync | `Task<WorkflowDefinition> UpsertDefinitionAsync(WorkflowDefinition def)` |
| GetDefinitionAsync | `Task<WorkflowDefinition?> GetDefinitionAsync(Guid id)` |
| ListDefinitionsAsync | `Task<List<WorkflowDefinition>> ListDefinitionsAsync()` |
| CreateInstanceAsync | `Task<WorkflowInstance> CreateInstanceAsync(WorkflowInstance instance)` |
| UpdateInstanceAsync | `Task<WorkflowInstance?> UpdateInstanceAsync(Guid id, Action<WorkflowInstance> update)` |
| GetInstanceAsync | `Task<WorkflowInstance?> GetInstanceAsync(Guid id)` |
| DeleteInstanceAsync | `Task<bool> DeleteInstanceAsync(Guid id)` |
| ListInstancesAsync | `Task<(List<WorkflowInstance> Instances, int Total)> ListInstancesAsync(Guid? definitionId, Guid? tenantId, int page, int pageSize)` |

**File**: `Tamma.Data/Repositories/IEventRepository.cs`

| Method | Signature |
|---|---|
| AppendAsync | `Task<DomainEvent> AppendAsync(DomainEvent evt)` |
| GetByIdAsync | `Task<DomainEvent?> GetByIdAsync(Guid id)` |
| QueryAsync | `Task<List<DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)` |
| GetLastByTypeAsync | `Task<DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)` |
| ClearAsync | `Task ClearAsync(Guid tenantId)` |

### 2.2 Implementations

Create one implementation file per interface in `Tamma.Data/Repositories/`.
Name: `UserRepository.cs`, `TenantRepository.cs`, etc.

Each implementation:
- Injects `TammaDbContext`
- Uses `async` LINQ queries (`ToListAsync`, `FirstOrDefaultAsync`, `CountAsync`)
- Calls `_db.SaveChangesAsync()` after mutations
- Does NOT catch exceptions (let middleware handle 500s)

### 2.3 DI Registration

**File**: `Tamma.Data/DependencyInjection.cs`

```csharp
namespace Tamma.Data;

public static class DependencyInjection
{
    public static IServiceCollection AddTammaData(this IServiceCollection services, string connectionString)
    {
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddDbContext<TammaDbContext>(options => options.UseNpgsql(connectionString));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IPasswordResetRepository, PasswordResetRepository>();
        services.AddScoped<ITenantRepository, TenantRepository>();
        services.AddScoped<ITenantMembershipRepository, TenantMembershipRepository>();
        services.AddScoped<IInviteRepository, InviteRepository>();
        services.AddScoped<IApiKeyRepository, ApiKeyRepository>();
        services.AddScoped<IInstallationRepository, InstallationRepository>();
        services.AddScoped<IAgentConfigRepository, AgentConfigRepository>();
        services.AddScoped<IPromptRepository, PromptRepository>();
        services.AddScoped<IProviderHealthRepository, ProviderHealthRepository>();
        services.AddScoped<IDiagnosticsRepository, DiagnosticsRepository>();
        services.AddScoped<ISanitizationRepository, SanitizationRepository>();
        services.AddScoped<IWorkflowRepository, WorkflowRepository>();
        services.AddScoped<IEventRepository, EventRepository>();

        return services;
    }
}
```

---

## Task 3: Auth Infrastructure

### 3.1 Password Hashing

**File**: `Tamma.Api/Auth/PasswordService.cs`
**NuGet**: `Konscious.Security.Cryptography.Argon2` (add to `Tamma.Api.csproj`)

```csharp
public interface IPasswordService
{
    string HashPassword(string password);
    bool VerifyPassword(string password, string hash);
}
```

Implementation uses Argon2id with:
- Memory: 65536 KB (64 MB)
- Iterations: 3
- Parallelism: 4
- Salt: 16 random bytes
- Hash length: 32 bytes
- Output format: `$argon2id$v=19$m=65536,t=3,p=4$<salt-base64>$<hash-base64>`

### 3.2 JWT Service

**File**: `Tamma.Api/Auth/JwtService.cs`
**NuGet**: Already present (`Microsoft.AspNetCore.Authentication.JwtBearer`)
Additional: `System.IdentityModel.Tokens.Jwt` (add to `Tamma.Api.csproj`)

```csharp
public interface IJwtService
{
    string GenerateAccessToken(User user, Guid tenantId, string role);
    string GenerateRefreshToken();
    ClaimsPrincipal? ValidateToken(string token);
}
```

JWT claims:
- `sub`: user ID (GUID string)
- `tid`: tenant ID (GUID string)
- `role`: member | admin | owner
- `email`: user email
- `jti`: unique token ID (GUID)
- `iss`: "tamma"
- `aud`: "tamma-api"
- `exp`: now + 15 minutes (access) or now + 7 days (refresh)
- `iat`: now

Signing: HMAC-SHA256 with secret from `Jwt:Secret` configuration.

Cookie config for `tamma_session`:
- HttpOnly: true
- Secure: true
- SameSite: Lax
- Path: /
- MaxAge: 7 days (matches refresh token)

### 3.3 API Key Auth Handler

**File**: `Tamma.Api/Auth/ApiKeyAuthHandler.cs`

Custom `AuthenticationHandler<AuthenticationSchemeOptions>`:
- Reads `Authorization: ApiKey <key>` header
- Hashes the key with SHA-256
- Looks up via `IApiKeyRepository.GetByHashAsync`
- Creates `ClaimsPrincipal` with scope claims
- Checks `RevokedAt` and rotation grace period

Register as a second authentication scheme alongside JWT:

```csharp
builder.Services.AddAuthentication()
    .AddJwtBearer("Bearer", options => { ... })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>("ApiKey", null);

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes("Bearer", "ApiKey")
        .RequireAuthenticatedUser()
        .Build();
});
```

### 3.4 RBAC Permissions

**File**: `Tamma.Api/Auth/Permissions.cs`

Port the permission matrix from `packages/api/src/auth/permissions.ts`:

```csharp
public static class Permissions
{
    public static readonly Dictionary<string, string[]> Matrix = new()
    {
        ["dashboard:view"]  = ["member", "admin", "owner"],
        ["workflows:view"]  = ["member", "admin", "owner"],
        ["workflows:manage"] = ["admin", "owner"],
        ["workflows:delete"] = ["owner"],
        ["users:view"]      = ["admin", "owner"],
        ["users:manage"]    = ["owner"],
        ["admin:access"]    = ["admin", "owner"],
        ["logs:access"]     = ["admin", "owner"],
        ["elsa:access"]     = ["admin", "owner"],
        ["settings:view"]   = ["admin", "owner"],
        ["settings:manage"] = ["owner"],
        ["apikeys:manage"]  = ["admin", "owner"],
    };

    public static bool HasPermission(string role, string permission) { ... }
    public static string[] GetRolePermissions(string role) { ... }
}
```

### 3.5 Authorization Policies

**File**: `Tamma.Api/Auth/PermissionHandler.cs`

Custom `IAuthorizationHandler` that checks the `role` claim against the
permission matrix:

```csharp
public class PermissionRequirement : IAuthorizationRequirement
{
    public string Permission { get; }
    public PermissionRequirement(string permission) => Permission = permission;
}

public class PermissionHandler : AuthorizationHandler<PermissionRequirement> { ... }
```

Register policies in `Program.cs`:

```csharp
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminAccess", p => p.AddRequirements(new PermissionRequirement("admin:access")));
    options.AddPolicy("OwnerAccess", p => p.AddRequirements(new PermissionRequirement("users:manage")));
    options.AddPolicy("MemberAccess", p => p.RequireAuthenticatedUser());
    options.AddPolicy("SettingsView", p => p.AddRequirements(new PermissionRequirement("settings:view")));
    options.AddPolicy("SettingsManage", p => p.AddRequirements(new PermissionRequirement("settings:manage")));
    options.AddPolicy("WorkflowsView", p => p.AddRequirements(new PermissionRequirement("workflows:view")));
    options.AddPolicy("WorkflowsManage", p => p.AddRequirements(new PermissionRequirement("workflows:manage")));
    options.AddPolicy("DashboardView", p => p.AddRequirements(new PermissionRequirement("dashboard:view")));
    options.AddPolicy("ApiKeysManage", p => p.AddRequirements(new PermissionRequirement("apikeys:manage")));
});
```

### 3.6 Login Lockout Service

**File**: `Tamma.Api/Services/LoginLockoutService.cs`

In-memory implementation (same as TS):
- 5 failed attempts in 15 minutes triggers 30-minute lockout
- Per-email tracking
- Successful login resets counter

```csharp
public interface ILoginLockoutService
{
    bool RecordFailedAttempt(string email);
    bool IsLocked(string email);
    void ResetAttempts(string email);
    int GetRemainingLockoutSeconds(string email);
}
```

Register as singleton (in-memory state shared across requests).

### 3.7 NuGet Packages for Auth

Add to `Tamma.Api/Tamma.Api.csproj`:

```xml
<PackageReference Include="Konscious.Security.Cryptography.Argon2" Version="1.3.1" />
<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.3.0" />
```

`Microsoft.AspNetCore.Authentication.JwtBearer` is already present.

---

## Task 4: Middleware Pipeline

### 4.1 TenantContextMiddleware

**File**: `Tamma.Api/Middleware/TenantContextMiddleware.cs`

```csharp
public class TenantContextMiddleware
{
    private static readonly HashSet<string> TenantFreePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/health",
        "/api/v1/auth/register",
        "/api/v1/auth/login",
        "/api/v1/auth/verify-email",
        "/api/v1/auth/password-reset/request",
        "/api/v1/auth/password-reset/confirm",
        "/api/auth/github",
        "/api/auth/github/callback",
        "/api/github/callback",
        "/api/github/webhooks",
        "/health",
    };

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        // Skip tenant-free paths
        // Extract tid claim from JWT or resolve from API key principal
        // Set tenantContext.SetTenantId(...)
        // 403 if tenant cannot be resolved
    }
}
```

### 4.2 EnsurePersonalTenantMiddleware

**File**: `Tamma.Api/Middleware/EnsurePersonalTenantMiddleware.cs`

Runs after TenantContextMiddleware. For authenticated users without a tenant:
1. Check if user has existing memberships; if so, pick most recent
2. Otherwise, auto-create personal tenant and add as owner
3. Idempotent: short-circuits if user already has tenant

### 4.3 Pipeline Order in Program.cs

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();
app.UseMiddleware<EnsurePersonalTenantMiddleware>();
// ... endpoint mapping follows
```

---

## Task 5: Endpoint Groups

All endpoints use Minimal API with `MapGroup()`. One static class per group.
Each file lives in `Tamma.Api/Endpoints/`.

### 5.1 Health + Admin Group

**File**: `Tamma.Api/Endpoints/HealthEndpoints.cs`

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/health` | None | `{ status: "ok", timestamp }` |
| GET | `/health` | None | ASP.NET health checks (Npgsql) |

**File**: `Tamma.Api/Endpoints/AdminEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/admin/health` | AdminAccess | Health check with DB stats |
| POST | `/api/admin/service-keys` | AdminAccess | IApiKeyRepository |
| GET | `/api/admin/service-keys` | AdminAccess | IApiKeyRepository |
| POST | `/api/admin/service-keys/{id}/rotate` | AdminAccess | IApiKeyRepository |
| DELETE | `/api/admin/service-keys/{id}` | AdminAccess | IApiKeyRepository |
| GET | `/api/admin/users` | AdminAccess | IUserRepository |
| GET | `/api/admin/users/{id}` | AdminAccess | IUserRepository |
| PUT | `/api/admin/users/{id}/role` | OwnerAccess | IUserRepository, ITenantMembershipRepository |
| DELETE | `/api/admin/users/{id}` | OwnerAccess | IUserRepository |
| POST | `/api/admin/users/invite` | AdminAccess | IInviteRepository |
| GET | `/api/admin/users/invites` | AdminAccess | IInviteRepository |
| DELETE | `/api/admin/users/invites/{id}` | AdminAccess | IInviteRepository |
| POST | `/api/admin/users/{id}/keys` | ApiKeysManage | IApiKeyRepository |
| GET | `/api/admin/users/{id}/keys` | ApiKeysManage | IApiKeyRepository |
| DELETE | `/api/admin/users/{id}/keys/{keyId}` | ApiKeysManage | IApiKeyRepository |

**Request/Response DTOs**: `Tamma.Api/Dtos/Admin/`
- `CreateServiceKeyRequest` { label, permissions[] }
- `ServiceKeyResponse` { id, label, prefix, permissions[], createdAt } (never returns raw key except on create)
- `UpdateUserRoleRequest` { role }
- `InviteUserRequest` { email, role }
- `CreateUserApiKeyRequest` { label }

### 5.2 Auth Group

**File**: `Tamma.Api/Endpoints/AuthEndpoints.cs`

| Method | Path | Auth | Repository / Service |
|---|---|---|---|
| POST | `/api/v1/auth/register` | None | IUserRepository, IPasswordService |
| POST | `/api/v1/auth/verify-email` | None | IUserRepository |
| POST | `/api/v1/auth/resend-verification` | None | IUserRepository |
| POST | `/api/v1/auth/login` | None | IUserRepository, IPasswordService, IJwtService, ILoginLockoutService |
| POST | `/api/v1/auth/refresh` | None (cookie) | IRefreshTokenRepository, IJwtService |
| POST | `/api/v1/auth/logout` | MemberAccess | IRefreshTokenRepository |
| POST | `/api/v1/auth/password-reset/request` | None | IPasswordResetRepository |
| POST | `/api/v1/auth/password-reset/confirm` | None | IPasswordResetRepository, IPasswordService, IRefreshTokenRepository |
| GET | `/api/auth/me` | MemberAccess | IUserRepository, ITenantMembershipRepository |
| GET | `/api/auth/role-check` | MemberAccess | (reads claims) |
| GET | `/api/auth/github` | None | Redirect to GitHub OAuth |
| GET | `/api/auth/github/callback` | None | IUserRepository, IJwtService, IInstallationRepository |

**DTOs**: `Tamma.Api/Dtos/Auth/`
- `RegisterRequest` { email, password, displayName }
- `RegisterResponse` { userId, message }
- `LoginRequest` { email, password }
- `LoginResponse` { accessToken, expiresIn, user }
- `RefreshResponse` { accessToken, expiresIn }
- `PasswordResetRequestDto` { email }
- `PasswordResetConfirmDto` { token, newPassword }
- `MeResponse` { id, email, displayName, role, tenantId, memberships[] }

### 5.3 Organization / Tenant Group

**File**: `Tamma.Api/Endpoints/OrgEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| POST | `/api/v1/orgs` | MemberAccess | ITenantRepository, ITenantMembershipRepository |
| GET | `/api/v1/orgs/{tenantId}` | MemberAccess | ITenantRepository |
| PUT | `/api/v1/orgs/{tenantId}/settings` | SettingsManage | ITenantRepository |
| GET | `/api/v1/orgs/{tenantId}/members` | MemberAccess | ITenantMembershipRepository |
| PUT | `/api/v1/orgs/{tenantId}/members/{userId}/role` | AdminAccess | ITenantMembershipRepository |
| DELETE | `/api/v1/orgs/{tenantId}/members/{userId}` | AdminAccess | ITenantMembershipRepository |
| POST | `/api/v1/orgs/{tenantId}/invites` | AdminAccess | IInviteRepository |
| GET | `/api/v1/orgs/{tenantId}/invites` | AdminAccess | IInviteRepository |
| DELETE | `/api/v1/orgs/{tenantId}/invites/{inviteId}` | AdminAccess | IInviteRepository |
| POST | `/api/v1/orgs/invites/accept` | MemberAccess | IInviteRepository, ITenantMembershipRepository |
| POST | `/api/v1/auth/switch-org` | MemberAccess | ITenantMembershipRepository, IJwtService |
| GET | `/api/v1/tenants` | MemberAccess | ITenantRepository |
| POST | `/api/v1/orgs/{tenantId}/transfer-ownership` | OwnerAccess | ITenantRepository, ITenantMembershipRepository |
| DELETE | `/api/v1/orgs/{tenantId}` | OwnerAccess | ITenantRepository |

**DTOs**: `Tamma.Api/Dtos/Orgs/`
- `CreateOrgRequest` { name, slug }
- `UpdateOrgSettingsRequest` { settings (object) }
- `UpdateMemberRoleRequest` { role }
- `CreateOrgInviteRequest` { email, role }
- `AcceptInviteRequest` { token }
- `TransferOwnershipRequest` { newOwnerId }
- `OrgResponse` { id, name, slug, type, ownerId, settings, createdAt }
- `MemberResponse` { userId, role, joinedAt, displayName, email }

### 5.4 Agent Config Group

**File**: `Tamma.Api/Endpoints/AgentEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/v1/agents/config` | SettingsView | IAgentConfigRepository |
| PUT | `/api/v1/agents/config` | SettingsManage | IAgentConfigRepository |
| POST | `/api/v1/agents/config/validate` | SettingsView | (validation logic) |
| GET | `/api/v1/agents/{role}/resolve` | MemberAccess | IAgentConfigRepository |
| POST | `/api/v1/agents/resolve-for-phase` | MemberAccess | IAgentConfigRepository |

**DTOs**: `Tamma.Api/Dtos/Agents/`
- `UpdateAgentConfigRequest` { config (object) }
- `ValidateConfigRequest` { config (object) }
- `ResolveForPhaseRequest` { phase, taskType }
- `AgentConfigResponse` { config, source, version }
- `ResolvedAgentResponse` { provider, model, config }

### 5.5 Prompt Group

**File**: `Tamma.Api/Endpoints/PromptEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/prompts` | SettingsView | IPromptRepository |
| GET | `/api/prompts/system` | SettingsView | (DefaultPrompts static) |
| GET | `/api/prompts/system/{role}/{action}` | SettingsView | (DefaultPrompts static) |
| GET | `/api/prompts/{role}/{action}` | SettingsView | IPromptRepository |
| PUT | `/api/prompts/{role}/{action}` | SettingsManage | IPromptRepository |
| DELETE | `/api/prompts/{role}/{action}` | SettingsManage | IPromptRepository |
| PUT | `/api/prompts/system/{role}/{action}` | SettingsManage | IPromptRepository |
| DELETE | `/api/prompts/system/{role}/{action}` | SettingsManage | IPromptRepository |
| POST | `/api/prompts/{role}/{action}/render` | SettingsView | IPromptRepository |

**DTOs**: `Tamma.Api/Dtos/Prompts/`
- `UpsertPromptRequest` { template, systemPrompt, variables[], enableTools, maxTokens }
- `RenderPromptRequest` { variables (dict) }
- `PromptResponse` { role, action, template, systemPrompt, variables[], enableTools, maxTokens, source }
- `RenderedPromptResponse` { systemPrompt, userPrompt }

**File**: `Tamma.Api/Endpoints/ConventionEndpoints.cs`

| Method | Path | Auth | Notes |
|---|---|---|---|
| GET | `/api/convention-templates` | None | List all convention templates |
| GET | `/api/convention-templates/{key}` | None | Get single template |

**Static data file**: `Tamma.Api/Data/DefaultPrompts.cs`
Port the 8 role prompts, 10 action templates, 80 role+action templates from
`packages/api/src/services/default-prompts.ts`.

**Static data file**: `Tamma.Api/Data/ConventionTemplates.cs`
Port the 20 convention templates.

### 5.6 Settings Group

**File**: `Tamma.Api/Endpoints/SettingsEndpoints.cs`

Config subgroup (`/api/config/*`):

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/config/agents` | SettingsView | IAgentConfigRepository |
| PUT | `/api/config/agents` | SettingsManage | IAgentConfigRepository |
| GET | `/api/config/security` | SettingsView | IAgentConfigRepository (security section) |
| PUT | `/api/config/security` | SettingsManage | IAgentConfigRepository |
| POST | `/api/config/sanitize` | SettingsManage | ISanitizationRepository |
| GET | `/api/config/sanitize/rules` | SettingsView | ISanitizationRepository |
| PUT | `/api/config/sanitize/rules` | SettingsManage | ISanitizationRepository |
| GET | `/api/config/prompts` | SettingsView | (reads agent config prompts section) |
| PUT | `/api/config/prompts/{role}` | SettingsManage | (updates agent config prompts section) |
| GET | `/api/config/providers` | SettingsView | (reads agent config providers section) |
| PUT | `/api/config/providers` | SettingsManage | (updates agent config providers section) |

**File**: `Tamma.Api/Endpoints/ProviderEndpoints.cs`

Provider subgroup (`/api/providers/*`):

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/providers/health` | SettingsView | IProviderHealthRepository |
| GET | `/api/providers/health/providers` | SettingsView | IProviderHealthRepository |
| GET | `/api/providers/health/providers/{key}` | SettingsView | IProviderHealthRepository |
| POST | `/api/providers/health/providers/{key}/failure` | SettingsManage | IProviderHealthRepository |
| POST | `/api/providers/health/providers/{key}/success` | SettingsManage | IProviderHealthRepository |
| POST | `/api/providers/health/providers/{key}/reset` | SettingsManage | IProviderHealthRepository |
| GET | `/api/providers/diagnostics` | SettingsView | IDiagnosticsRepository |
| GET | `/api/providers/diagnostics/query` | SettingsView | IDiagnosticsRepository |
| GET | `/api/providers/diagnostics/report` | SettingsView | IDiagnosticsRepository |
| GET | `/api/providers/diagnostics/budget/{accountId}` | SettingsView | IDiagnosticsRepository |
| POST | `/api/providers/diagnostics` | SettingsManage | IDiagnosticsRepository |
| POST | `/api/providers/providers/create` | SettingsManage | (provider session service) |
| POST | `/api/providers/providers/{handle}/execute` | SettingsManage | (provider session service) |
| DELETE | `/api/providers/providers/{handle}` | SettingsManage | (provider session service) |
| GET | `/api/providers/providers/sessions` | SettingsView | (provider session service) |

**DTOs**: `Tamma.Api/Dtos/Settings/`
- `UpdateAgentsConfigRequest` { config (object) }
- `UpdateSecurityConfigRequest` { config (object) }
- `UpdateSanitizationRulesRequest` { rules (object) }
- `SanitizeRequest` { content }
- `IngestDiagnosticRequest` { providerKey, durationMs, tokensUsed, cost, model, success, error }
- `CreateProviderRequest` { type, config }
- `ExecuteProviderRequest` { messages[], options }

### 5.7 Engine Group

**File**: `Tamma.Api/Endpoints/EngineEndpoints.cs`

| Method | Path | Auth Policy | Repository / Service |
|---|---|---|---|
| POST | `/api/engine/command` | WorkflowsManage | (engine service) |
| GET | `/api/engine/state` | WorkflowsView | IEventRepository |
| GET | `/api/engine/stats` | WorkflowsView | IEventRepository |
| GET | `/api/engine/plan` | WorkflowsView | (engine service) |
| GET | `/api/engine/history` | WorkflowsView | IEventRepository |
| GET | `/api/engine/events/state` | WorkflowsView | IEventRepository (polling) |
| GET | `/api/engine/events/logs` | WorkflowsView | IEventRepository (polling) |
| POST | `/api/engine/store-context` | WorkflowsManage | IEventRepository |
| GET | `/api/engine/context/{issueNumber}` | WorkflowsView | IEventRepository |
| POST | `/api/engine/query-context` | WorkflowsView | IEventRepository |
| GET | `/api/engine/repo-config` | WorkflowsView | (GitHub service) |
| GET | `/api/engine/issues` | WorkflowsView | (GitHub service) |
| GET | `/api/engine/security-alerts` | WorkflowsView | (GitHub service) |
| POST | `/api/engine/issue-comment` | WorkflowsManage | (GitHub service) |
| POST | `/api/engine/issue-labels` | WorkflowsManage | (GitHub service) |
| DELETE | `/api/engine/issue-labels/{repo}/{issueNumber}/{label}` | WorkflowsManage | (GitHub service) |
| POST | `/api/engine/create-issue` | WorkflowsManage | (GitHub service) |
| POST | `/api/engine/trigger-ci` | WorkflowsManage | (GitHub service) |
| POST | `/api/engine/execute-task` | WorkflowsManage | (task execution service) |
| POST | `/api/engine/cycle-result` | WorkflowsManage | IEventRepository |
| GET | `/api/engine/cycle-results` | WorkflowsView | IEventRepository |
| POST | `/api/engine/agent-available` | WorkflowsManage | (engine registry) |

The former SSE endpoints (`events/state`, `events/logs`) become polling endpoints
that return the latest N events. No SignalR for now.

**DTOs**: `Tamma.Api/Dtos/Engine/`
- `SendCommandRequest` { command, args }
- `StoreContextRequest` { issueNumber, context (object) }
- `QueryContextRequest` { query }
- `IssueCommentRequest` { repo, issueNumber, body }
- `IssueLabelRequest` { repo, issueNumber, labels[] }
- `CreateIssueRequest` { repo, title, body, labels[] }
- `TriggerCiRequest` { repo, ref, workflow }
- `ExecuteTaskRequest` { taskType, context (object) }
- `CycleResultRequest` { issueNumber, result (object) }
- `AgentAvailableRequest` { engineId, capabilities }

### 5.8 Workflow Group

**File**: `Tamma.Api/Endpoints/WorkflowEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| POST | `/api/workflows/definitions` | WorkflowsManage | IWorkflowRepository |
| GET | `/api/workflows/definitions` | WorkflowsView | IWorkflowRepository |
| POST | `/api/workflows/instances` | WorkflowsManage | IWorkflowRepository |
| PUT | `/api/workflows/instances/{id}` | WorkflowsManage | IWorkflowRepository |
| GET | `/api/workflows/instances` | WorkflowsView | IWorkflowRepository |
| POST | `/api/workflows/instances/{id}/cancel` | WorkflowsManage | IWorkflowRepository |
| DELETE | `/api/workflows/instances/{id}` | WorkflowsManage | IWorkflowRepository |
| GET | `/api/workflows/instances/{id}/events` | WorkflowsView | IEventRepository (polling, no SSE) |

**DTOs**: `Tamma.Api/Dtos/Workflows/`
- `CreateDefinitionRequest` { name, description, steps (object) }
- `CreateInstanceRequest` { definitionId, variables (object) }
- `UpdateInstanceRequest` { status, currentActivity, variables }
- `DefinitionResponse` { id, name, version, description, syncedAt }
- `InstanceResponse` { id, definitionId, status, currentActivity, createdAt, updatedAt }

### 5.9 GitHub App Group

**File**: `Tamma.Api/Endpoints/GitHubEndpoints.cs`

| Method | Path | Auth | Repository |
|---|---|---|---|
| GET | `/api/github/callback` | None | IInstallationRepository, IUserRepository |
| POST | `/api/github/webhooks` | None (signature verification) | IInstallationRepository |

Webhook handler verifies the `X-Hub-Signature-256` header using HMAC-SHA256.
Handles events: `installation`, `installation_repositories`, `push`, `issues`.

### 5.10 SaaS Group

**File**: `Tamma.Api/Endpoints/SaaSEndpoints.cs`

| Method | Path | Auth | Repository |
|---|---|---|---|
| POST | `/api/v1/llm/chat` | ApiKey (engine:write) | (LLM proxy service) |
| POST | `/api/v1/workflows/{id}/status` | ApiKey (engine:write) | IWorkflowRepository |
| POST | `/api/v1/workflows/{id}/result` | ApiKey (engine:write) | IWorkflowRepository, IEventRepository |
| POST | `/api/v1/installations/{id}/rotate-key` | ApiKey (admin:write) | IApiKeyRepository |

### 5.11 Dashboard Group

**File**: `Tamma.Api/Endpoints/DashboardEndpoints.cs`

| Method | Path | Auth Policy | Repository |
|---|---|---|---|
| GET | `/api/dashboard/summary` | DashboardView | IEventRepository, IWorkflowRepository |
| GET | `/api/dashboard/engines` | DashboardView | (engine registry) |
| GET | `/api/dashboard/workflows` | DashboardView | IWorkflowRepository |

### 5.12 Knowledge Base Group

**File**: `Tamma.Api/Endpoints/KbEndpoints.cs`

30 routes under `/api/kb/*`. All return stub/mock responses initially.
Real implementations will be wired when the intelligence packages are built.

| Prefix | Routes | Stub Behavior |
|---|---|---|
| `/api/kb/index/*` | 6 routes | Return empty status, no-op triggers |
| `/api/kb/vector-db/*` | 6 routes | Return empty collections, empty search |
| `/api/kb/rag/*` | 4 routes | Return empty config, zero metrics |
| `/api/kb/mcp/*` | 8 routes | Return empty server list, no-op start/stop |
| `/api/kb/context/*` | 3 routes | Return empty history, no-op feedback |
| `/api/kb/analytics/*` | 3 routes | Return zero-value analytics |

Auth: GET routes require `SettingsView`, mutation routes require `SettingsManage`.

### 5.13 Endpoint Registration in Program.cs

```csharp
// Health (no auth)
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", timestamp = DateTime.UtcNow }));

// Auth (no auth required on most)
var auth = app.MapGroup("/api/v1/auth");
auth.MapPost("/register", AuthEndpoints.Register);
auth.MapPost("/verify-email", AuthEndpoints.VerifyEmail);
// ... etc

// Admin (requires AdminAccess)
var admin = app.MapGroup("/api/admin").RequireAuthorization("AdminAccess");
admin.MapGet("/health", AdminEndpoints.GetHealth);
// ... etc

// Orgs
var orgs = app.MapGroup("/api/v1/orgs").RequireAuthorization("MemberAccess");
// ...

// Settings - config
var config = app.MapGroup("/api/config").RequireAuthorization("SettingsView");
// GET endpoints use SettingsView, PUT/POST use endpoint-level SettingsManage

// Settings - providers
var providers = app.MapGroup("/api/providers").RequireAuthorization("SettingsView");

// Engine
var engine = app.MapGroup("/api/engine").RequireAuthorization("WorkflowsView");

// Workflows
var workflows = app.MapGroup("/api/workflows").RequireAuthorization("WorkflowsView");

// KB
var kb = app.MapGroup("/api/kb").RequireAuthorization("SettingsView");

// GitHub (no auth, uses webhook signature)
var github = app.MapGroup("/api/github");

// SaaS (API key auth)
var saas = app.MapGroup("/api/v1").RequireAuthorization("ApiKey");

// Dashboard
var dashboard = app.MapGroup("/api/dashboard").RequireAuthorization("DashboardView");

// Convention templates (no auth)
app.MapGet("/api/convention-templates", ConventionEndpoints.ListAll);
app.MapGet("/api/convention-templates/{key}", ConventionEndpoints.GetByKey);
```

---

## Task 6: Test Strategy

### 6.1 Test Project Setup

**Project**: `apps/tamma-elsa/tests/Tamma.Api.Tests/` (already exists)

Add NuGet packages to `Tamma.Api.Tests.csproj`:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.0" />
<PackageReference Include="Testcontainers.PostgreSql" Version="4.3.0" />
<PackageReference Include="FluentAssertions" Version="7.0.0" />
<PackageReference Include="xunit" Version="2.9.3" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
```

### 6.2 Test Infrastructure

**File**: `tests/Tamma.Api.Tests/Fixtures/PostgresFixture.cs`

Shared Testcontainers fixture that starts a PostgreSQL container once per test
collection. Runs migrations automatically.

```csharp
public class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        // Apply migrations via DbContext
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
```

**File**: `tests/Tamma.Api.Tests/Fixtures/TammaWebApplicationFactory.cs`

Custom `WebApplicationFactory<Program>` that:
- Replaces the connection string with Testcontainers PostgreSQL
- Configures test JWT secret
- Seeds a test user and tenant
- Provides helper methods for authenticated HTTP requests

### 6.3 Test File Structure

```
tests/Tamma.Api.Tests/
├── Fixtures/
│   ├── PostgresFixture.cs
│   └── TammaWebApplicationFactory.cs
├── Unit/
│   ├── Auth/
│   │   ├── PasswordServiceTests.cs      (~10 tests)
│   │   ├── JwtServiceTests.cs           (~10 tests)
│   │   ├── PermissionsTests.cs          (~10 tests)
│   │   └── LoginLockoutTests.cs         (~8 tests)
│   ├── Middleware/
│   │   ├── TenantContextMiddlewareTests.cs     (~8 tests)
│   │   └── EnsurePersonalTenantTests.cs        (~7 tests)
│   └── Data/
│       └── DefaultPromptsTests.cs       (~5 tests)
├── Integration/
│   ├── Repositories/
│   │   ├── UserRepositoryTests.cs       (~8 tests)
│   │   ├── TenantRepositoryTests.cs     (~6 tests)
│   │   ├── TenantMembershipTests.cs     (~6 tests)
│   │   ├── ApiKeyRepositoryTests.cs     (~6 tests)
│   │   ├── RefreshTokenRepositoryTests.cs (~5 tests)
│   │   ├── InviteRepositoryTests.cs     (~5 tests)
│   │   ├── WorkflowRepositoryTests.cs   (~5 tests)
│   │   ├── EventRepositoryTests.cs      (~5 tests)
│   │   └── TenantIsolationTests.cs      (~10 tests, cross-tenant verification)
│   └── Endpoints/
│       ├── AuthEndpointTests.cs         (~40 tests)
│       ├── AdminEndpointTests.cs        (~35 tests)
│       ├── OrgEndpointTests.cs          (~30 tests)
│       ├── AgentEndpointTests.cs        (~10 tests)
│       ├── PromptEndpointTests.cs       (~20 tests)
│       ├── SettingsEndpointTests.cs     (~25 tests)
│       ├── EngineEndpointTests.cs       (~20 tests)
│       ├── WorkflowEndpointTests.cs     (~15 tests)
│       ├── GitHubEndpointTests.cs       (~5 tests)
│       ├── SaaSEndpointTests.cs         (~5 tests)
│       ├── DashboardEndpointTests.cs    (~5 tests)
│       └── KbEndpointTests.cs           (~5 tests)
```

**Total**: ~320 tests

### 6.4 Key Test Patterns

**Tenant isolation test** (critical):

```csharp
[Fact]
public async Task TenantA_CannotSee_TenantB_Data()
{
    // Create user in Tenant A
    // Create user in Tenant B
    // Query as Tenant A -> should NOT see Tenant B user
    // Query as admin (null tenant) -> should see BOTH
}
```

**Auth endpoint test**:

```csharp
[Fact]
public async Task Register_Login_Refresh_Logout_Flow()
{
    var client = _factory.CreateClient();
    // POST /api/v1/auth/register -> 201
    // POST /api/v1/auth/login -> 200, check JWT + cookie
    // POST /api/v1/auth/refresh -> 200, new access token
    // POST /api/v1/auth/logout -> 200
    // POST /api/v1/auth/refresh -> 401 (token revoked)
}
```

### 6.5 Run Tests

```bash
cd apps/tamma-elsa
dotnet test tests/Tamma.Api.Tests/ --logger "console;verbosity=detailed"
```

---

## Task 7: Dockerfile

**File**: `apps/tamma-elsa/src/Tamma.Api/Dockerfile`

Update the existing Dockerfile to listen on port 3100 (matching TS API):

```dockerfile
# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy project files for layer-cached restore
COPY ["Tamma.Api/Tamma.Api.csproj", "Tamma.Api/"]
COPY ["Tamma.Core/Tamma.Core.csproj", "Tamma.Core/"]
COPY ["Tamma.Data/Tamma.Data.csproj", "Tamma.Data/"]
COPY ["Tamma.Activities/Tamma.Activities.csproj", "Tamma.Activities/"]

RUN dotnet restore "Tamma.Api/Tamma.Api.csproj"

# Copy source code
COPY . .

# Build and publish
WORKDIR "/src/Tamma.Api"
RUN dotnet publish "Tamma.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*
RUN groupadd -r tamma && useradd -r -g tamma tamma

COPY --from=publish /app/publish .
RUN mkdir -p /app/logs && chown -R tamma:tamma /app

USER tamma

EXPOSE 3100

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD curl -f http://localhost:3100/api/health || exit 1

ENV ASPNETCORE_URLS=http://+:3100
ENV ASPNETCORE_ENVIRONMENT=Production

ENTRYPOINT ["dotnet", "Tamma.Api.dll"]
```

Build context: `apps/tamma-elsa/src/`

```bash
docker build -t tamma-api:latest -f src/Tamma.Api/Dockerfile src/
```

---

## Task Summary

| Task | Files Created/Modified | Estimated Hours |
|---|---|---|
| 1. Entities + DbContext + Migration | 18 entity files, TammaDbContext.cs, TenantContext.cs, migration | 10h |
| 2. Repository Layer | 15 interface files, 15 implementation files, DI registration | 10h |
| 3. Auth Infrastructure | PasswordService, JwtService, ApiKeyAuthHandler, Permissions, PermissionHandler, LoginLockout | 12h |
| 4. Middleware | TenantContextMiddleware, EnsurePersonalTenantMiddleware, Program.cs pipeline | 4h |
| 5. Endpoint Groups | 12 endpoint files, ~40 DTO files, 2 static data files, Program.cs registration | 44h |
| 6. Test Suite | Fixtures, ~25 test files, ~320 tests | 16h |
| 7. Dockerfile | Update existing Dockerfile | 4h |
| **Total** | | **100h** |

---

## NuGet Package Summary

**Add to `Tamma.Api.csproj`**:

| Package | Version | Purpose |
|---|---|---|
| `Konscious.Security.Cryptography.Argon2` | 1.3.1 | Argon2id password hashing |
| `System.IdentityModel.Tokens.Jwt` | 8.3.0 | JWT creation and validation |

Already present: `Microsoft.AspNetCore.Authentication.JwtBearer`, `Swashbuckle.AspNetCore`,
`Serilog.AspNetCore`, `AspNetCore.HealthChecks.NpgSql`.

**Add to `Tamma.Api.Tests.csproj`**:

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.AspNetCore.Mvc.Testing` | 8.0.0 | WebApplicationFactory |
| `Testcontainers.PostgreSql` | 4.3.0 | Real PostgreSQL in tests |
| `FluentAssertions` | 7.0.0 | Readable assertions |

---

## Build Commands

```bash
# Restore + build
cd apps/tamma-elsa
dotnet build -c Release

# Run migrations
cd src
dotnet ef migrations add InitialCreate --project Tamma.Data --startup-project Tamma.Api
dotnet ef database update --project Tamma.Data --startup-project Tamma.Api

# Run tests
cd ..
dotnet test tests/Tamma.Api.Tests/ -c Release --logger "console;verbosity=detailed"

# Docker build
docker build -t tamma-api:latest -f src/Tamma.Api/Dockerfile src/

# Run locally
cd src/Tamma.Api
dotnet run -- --urls http://localhost:3100
```

---

## Dependency Order

```
Task 1 (Entities + DbContext)
  └─► Task 2 (Repositories)
       └─► Task 3 (Auth) + Task 4 (Middleware)
            └─► Task 5 (Endpoints)
                 └─► Task 6 (Tests) + Task 7 (Dockerfile)
```

Tasks 3 and 4 can run in parallel after Task 2.
Tasks 6 and 7 can run in parallel after Task 5.
