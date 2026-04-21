# Story 31-2: Platform registry + per-tenant platform routing resolver

Status: todo (planning brief, 2026-04-21)

## Story

As a **Tamma service component that needs to talk to a git platform
on behalf of a tenant**,
I want to hand off a `tenantId` and receive back a ready-to-use
`IGitPlatformDriver` wired to that tenant's selected platform +
installation credentials + webhook secrets,
so that the caller doesn't know or care whether the tenant uses
GitHub / Gitea / Forgejo / GitLab, and so that per-tenant credential
resolution goes through one well-audited code path.

## Narrative

Today, `IInstallationRouterService` resolves an installation id →
GitHub-App-scoped client. 31-2 generalises this to tenant-scoped
resolution across platforms.

Key shape:
```
IPlatformResolver.ResolveForTenantAsync(tenantId, ct)
  → IGitPlatformDriver
```

The resolver reads the tenant's `PlatformKind` from the
`tenant_platform_installations` table (new table or augmented
`github_installations`), decrypts the stored bot credentials via
the secret store (Epic 29), and returns a driver wired to the
tenant's endpoint + credentials + webhook-signature-verification key.

## Acceptance Criteria

1. New table `tenant_platform_installations` (migration new in this
   story) with columns: `id UUID`, `tenant_id UUID NOT NULL
   REFERENCES tenants(id)`, `platform_kind TEXT NOT NULL CHECK
   (platform_kind IN ('github','gitea','forgejo','gitlab',
   'bitbucket','azure_devops'))`, `base_url TEXT NOT NULL`,
   `installation_external_id TEXT NULL`, `credential_secret_id UUID
   NOT NULL REFERENCES tenant_secrets(id)`, `webhook_secret_id UUID
   NULL REFERENCES tenant_secrets(id)`, `status TEXT`, `metadata JSONB`,
   `created_at TIMESTAMPTZ`, `updated_at TIMESTAMPTZ`.
   Uniqueness: `(tenant_id, platform_kind, installation_external_id)`.
2. Existing `github_installations` rows are migrated in the same
   migration: each row creates a corresponding
   `tenant_platform_installations` row with `platform_kind='github'`
   and its `installation_external_id = installation_id`. Old table
   is **kept** for now (31-3 redirects reads).
3. `IPlatformResolver` interface:
   - `Task<IGitPlatformDriver?> ResolveForTenantAsync(Guid tenantId, CancellationToken ct)`
   - `Task<IGitPlatformDriver?> ResolveForWebhookAsync(Guid tenantPlatformInstallationId, CancellationToken ct)`
   - `Task<IEnumerable<IGitPlatformDriver>> ListForTenantAsync(Guid tenantId, CancellationToken ct)`
     (a tenant may eventually connect more than one platform —
     31-2 supports the shape now, even though the UI allows one in
     first cut).
4. Implementation `PlatformResolver`:
   - DI registry keyed by `PlatformKind` — each driver registers its
     factory at startup.
   - Credential load via `ISecretStore.GetAsync(credentialSecretId)`
     (Epic 29 seam).
   - Returns a driver instance with a `PlatformInstallation` context
     record so downstream code can log + audit which installation
     made the call.
   - In-memory LRU cache of `(tenantId → driver)` with 5-minute TTL
     + event-driven invalidation (subscribes to
     `TENANT.PLATFORM_CHANGED` via the event store tail).
5. RLS policy on `tenant_platform_installations`: app-role can only
   read rows matching `app.current_tenant_id`. Platform-admin role
   bypasses via `SET role postgres;` as per Epic 28 phase B.
6. Repository `ITenantPlatformInstallationRepository` with:
   `GetByTenantAsync`, `GetByIdAsync`, `CreateAsync`, `UpdateAsync`,
   `SoftDeleteAsync`, `ListByTenantAsync`.
7. New event types emitted on install connect + disconnect:
   `PLATFORM.INSTALLATION.CONNECTED.SUCCESS`,
   `PLATFORM.INSTALLATION.DISCONNECTED.SUCCESS`,
   `PLATFORM.INSTALLATION.CREDENTIAL_ROTATED.SUCCESS`. Tags include
   `tenantId`, `platformKind`, `installationId`.
8. Switch-org flow (Story 28-9): when the JWT's `tenantId` claim
   changes, the resolver cache is invalidated for that user's prior
   tenant by writing a `PLATFORM.RESOLVER_CACHE.INVALIDATED` event;
   the cache subscribes via an in-process event listener.
9. Unit tests cover:
   - resolver returns null for a tenant without a platform install
   - resolver caches the driver between calls within the TTL window
   - resolver invalidates on `PLATFORM.INSTALLATION.CREDENTIAL_ROTATED`
   - cross-tenant resolution — tenant A's resolver never returns
     tenant B's installation even with a spoofed id
10. Integration test stands up two fake drivers (Platform kind
    `Gitea` + `GitLab`), creates two tenants each with a different
    platform, asserts the resolver returns the correct driver per
    tenant.

## Technical Context

### Migration ordering

Runs **after** Epic 28 phase A (tenant DbContext factory) and **after**
Epic 29-2 (secret store exists) — the `credential_secret_id` FK
requires `tenant_secrets`.

### Cache invalidation

5-minute TTL is a compromise: a password/token rotation in 29-7 /
29-8 fires `PLATFORM.INSTALLATION.CREDENTIAL_ROTATED` which the
resolver subscribes to. A missed event (e.g. subscriber crash) still
self-heals in ≤5 minutes. Alternative: no TTL + strict event
dependency — rejected because it creates a cold-start availability
risk.

### Why not route by host header

Incoming webhooks (31-7) do route by host / path — the webhook's
path carries the platform kind. But outbound routing goes through
tenant context (the JWT) because the same tenant might have repos
on multiple hosts (e.g. a self-hosted Gitea + GitHub simultaneously
in a future story).

## Dependencies

- **31-1** — abstraction must exist
- **28-9** — switch-org (so cache invalidation handles tenant
  switches)
- **29-2** — secret store for credential resolution
- Blocks 31-3..31-9

## Estimated hours

**18h**

| Task | Hours |
|---|---|
| Migration + repo | 4 |
| `IPlatformResolver` + impl | 5 |
| Event emission + subscriber | 3 |
| LRU cache + invalidation | 2 |
| Tests (unit + integration) | 4 |

## Files touched

- `apps/tamma-elsa/src/Tamma.Data/Migrations/*_TenantPlatformInstallations.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Entities/TenantPlatformInstallation.cs` (new)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/TenantPlatformInstallationRepository.cs` (new)
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IPlatformResolver.cs` (new)
- `apps/tamma-elsa/src/Tamma.Platforms/PlatformResolver.cs` (new)
- `apps/tamma-elsa/tests/Tamma.Platforms.Tests/PlatformResolverTests.cs` (new)

## References

- Research notes: [`../research/multi-git-platform-2026.md`](../research/multi-git-platform-2026.md) §8
- Current installation router: `apps/tamma-elsa/src/Tamma.Api/Services/GitHub/InstallationRouterService.cs`
- Epic 29 secret store: [`../epic-29/29-1-secret-store-abstraction.md`](../epic-29/29-1-secret-store-abstraction.md)
