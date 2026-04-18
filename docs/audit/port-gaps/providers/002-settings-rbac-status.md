# Finding 002: RBAC on settings/provider/agent endpoints — status

**Scope**: providers
**Severity**: P2 (audit-summary reclassified: was P0 in `/tmp/tamma-audit/31-providers.md`)
**Status**: Incomplete (read-side coverage is present, but several surfaces diverge from the TS matrix)
**Estimated port effort**: 2–3h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/agents/index.ts` and
`git show 9e9a57c~1:packages/api/src/routes/settings/index.ts`.

- TS enforced RBAC via a Fastify `onRequest` hook installed at the route-group level and delegated to `requirePermission(permission)`. The exact matrix was method-based, not policy-based.

```typescript
// packages/api/src/routes/agents/index.ts (9e9a57c~1) — lines 46-52
scoped.addHook('onRequest', async (request, reply) => {
  if (request.method === 'PUT') {
    await requirePermission('settings:manage')(request, reply);
  } else {
    await requirePermission('settings:view')(request, reply);
  }
});
```

```typescript
// packages/api/src/routes/settings/index.ts (9e9a57c~1) — lines 61-68
instance.addHook('onRequest', async (request, reply) => {
  if (request.method === 'GET') {
    await requirePermission('settings:view')(request, reply);
  } else if (request.method === 'PUT' || request.method === 'POST') {
    await requirePermission('settings:manage')(request, reply);
  }
});
```

- `POST /api/v1/agents/config/validate` is classified by TS as "read-like" and requires `settings:view` (not `settings:manage`). See `agents/index.ts:46-52`.
- `POST /api/providers/diagnostics` (ingest) and `POST /sanitize` are `POST` and therefore require `settings:manage` under the TS hook matrix.
- Dependencies: `packages/api/src/auth/require-permission.ts` resolves the caller's role → permissions via the JWT `role` claim.

## 2. What's in C#

- File: `apps/tamma-elsa/src/Tamma.Api/Program.cs:373-427`
- Contract/behavior: Per-route `.RequireAuthorization("SettingsView")` at the map-group level, then per-endpoint `.RequireAuthorization("SettingsManage")` override on writes. Policies are registered at `Program.cs:190-242` against `PermissionRequirement("settings:view")` / `PermissionRequirement("settings:manage")`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) — lines 373-389
var agents = app.MapGroup("/api/v1/agents").RequireAuthorization("SettingsView");
agents.MapGet("/config", AgentEndpoints.GetConfig);
agents.MapPut("/config", AgentEndpoints.UpdateConfig).RequireAuthorization("SettingsManage");
agents.MapPost("/config/validate", AgentEndpoints.ValidateConfig);
agents.MapGet("/{role}/resolve", AgentEndpoints.ResolveAgent);
agents.MapPost("/resolve-for-phase", AgentEndpoints.ResolveForPhase);
```

```csharp
// apps/tamma-elsa/src/Tamma.Api/Program.cs (current) — lines 412-427
var providers = app.MapGroup("/api/providers").RequireAuthorization("SettingsView");
providers.MapGet("/health", ProviderEndpoints.GetHealthSummary);
...
providers.MapPost("/diagnostics", ProviderEndpoints.IngestDiagnostic).RequireAuthorization("SettingsManage");
providers.MapPost("/providers/create", ProviderEndpoints.CreateProvider).RequireAuthorization("SettingsManage");
providers.MapPost("/providers/{handle}/execute", ProviderEndpoints.ExecuteProvider).RequireAuthorization("SettingsManage");
```

- Development-mode override: `Program.cs:263-274` registers all policies as `AllowAnonymousRequirement` when no JWT secret is configured. This is explicit and logged, but it is distinct from TS behaviour (TS always enforced unless the request had an authenticated principal).
- Dependencies: `PermissionHandler` (`apps/tamma-elsa/src/Tamma.Api/Infrastructure/PermissionHandler.cs`), `AllowAnonymousRequirement` (`apps/tamma-elsa/src/Tamma.Api/Infrastructure/AllowAnonymousRequirement.cs`).

## 3. The gap

The RBAC story is stronger in C# than the `/tmp/tamma-audit/31-providers.md` summary claimed — the map-group + per-endpoint `.RequireAuthorization("...")` pattern is wired everywhere. Residual drift:

1. **`POST /providers/{handle}/execute` requires `settings:manage`** (`Program.cs:425`). TS did not treat provider execution as a write-config action; calling an LLM is a runtime operation and was gated via `settings:view` under the hook matrix (it is a POST, so actually it would have hit `settings:manage` in TS also — **no gap here**). Keep.
2. **`POST /api/providers/chain/resolve`** — C# has no `.RequireAuthorization("SettingsManage")` override, so it inherits the group `SettingsView` (`Program.cs:418`). TS classified this as `POST` → `settings:manage`. This is a privilege **loosening** on a read-only-semantics endpoint but violates the TS method-based matrix.
3. **`POST /api/config/sanitize`** — currently `SettingsManage` (`Program.cs:402`). This is a runtime sanitization call, not a rules update; TS hook gives it `settings:manage` because it's POST but TS also explicitly excluded `POST /api/v1/agents/config/validate` as "read-like". C# validate is **unauthenticated at the manage level** (inherits `SettingsView`), which matches the TS validate exemption — correct. But C# sanitize is still `SettingsManage`. Ideally both runtime POSTs (sanitize, chain/resolve) are `settings:view`.
4. **Per-tenant scoping vs role scoping**: TS `requirePermission` checks the caller's role claim but does not enforce that the caller's `tenantId` matches the target. C# `ITenantContext` is set from JWT, and endpoints scope by `tc.TenantId` — a **stronger** posture than TS had.
5. **Dev-mode anonymous**: the block at `Program.cs:263-274` means in Development, an unauthenticated request silently passes. TS had no equivalent escape hatch. Documented, but audit-worthy for staging deployments mis-set to `ASPNETCORE_ENVIRONMENT=Development`.

Error paths:
- TS: `401 {error:'Unauthenticated'}` or `403 {error:'Permission denied: settings:manage required'}` from `requirePermission`.
- C#: `401` for missing auth, `403` for missing claim (via ASP.NET default challenge/forbid responses); error body shape differs.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` AC 4 (RBAC implied on GET/PUT), `docs/stories/epic-16/16-5-role-based-access-control.md`, `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`.
- Story 16-5 matches the permission-name vocabulary the C# policies use (`settings:view`, `settings:manage`).
- Story alignment:
  - [x] Matches TS behavior on most surfaces.
  - [ ] Matches C# behavior.
  - [x] Describes a third behavior (stories didn't separate POST-runtime from POST-config-write).

## 5. Status

- **Classification**: Behavioral drift (minor).
- **What's needed to finish**:
  1. Downgrade `POST /api/providers/chain/resolve` to `SettingsView` (or leave at `SettingsManage` and update Story 9-5).
  2. Consider downgrading `POST /api/config/sanitize` to `SettingsView` for parity with TS "read-like" POST exemption philosophy (though TS did require `settings:manage` in practice).
  3. Document the Development-mode anonymous override prominently — pre-launch checklist.
  4. Add integration tests that exercise each route with a read-only role token.
- **Is it "just a stub" or is scope missing?** Scope is present; minor matrix drift only.
- **Blockers**: None.

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Api/Program.cs` (two lines)
- Files to create: none
- Tests to add:
  - `Program_ChainResolve_AllowsSettingsView`
  - `Program_UpdateConfig_Rejects_SettingsView`
  - `Program_DevMode_LogsExplicitAnonymousOverride`
- Estimated effort: 3h.

## References

- TS source: `packages/api/src/routes/agents/index.ts`, `packages/api/src/routes/settings/index.ts`, `packages/api/src/auth/require-permission.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Program.cs:185-274` (policies), `:373-427` (routes), `apps/tamma-elsa/src/Tamma.Api/Infrastructure/PermissionHandler.cs`
- Story: `docs/stories/epic-16/16-5-role-based-access-control.md`, `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`
- Related findings: `020-rate-limiting-missing-on-settings-routes.md`
- CLAUDE.md section: "API Endpoints" shows method-based gating convention.
