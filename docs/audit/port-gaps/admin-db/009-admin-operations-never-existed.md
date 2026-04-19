# Finding 009: Tenant impersonation / forced logout / lockdown / banners — **never existed in TS**

**Scope**: admin-db
**Severity**: P3 (documentation only)
**Status**: Not-yet-implemented — but not a regression
**Estimated port effort**: 0h (documentation); 40-60h if anyone ever decides to build them

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (documentation-only, not a regression)
- **Notes**: Both TS and C# admin surfaces never shipped these features. No code change required — the finding is preserved as a documentation artifact for future product discussion. Per the finding's own "What's needed to finish" section, building them is a 40-60h product spec exercise that is out of scope for the port-gap remediation.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/routes/admin/`.

- File: `packages/api/src/routes/admin/{index,health-routes,service-keys}.ts` — these three files are the *complete* admin surface. No other admin operations existed.
- Contract/behavior: the TS admin surface had two concerns — platform health visibility (finding 001) and service key CRUD (findings 003-008). That's it.
- Key code (verbatim quote, annotated):

```typescript
// packages/api/src/routes/admin/index.ts (9e9a57c~1)
export async function registerAdminRoutes(app: FastifyInstance, options?: AdminRouteOptions): Promise<void> {
  const healthOptions: AdminHealthOptions = {};
  if (options?.pgPool) healthOptions.pgPool = options.pgPool;
  registerAdminHealthRoutes(app, healthOptions);

  // Service key management routes (only if store is provided)
  if (options?.unifiedApiKeyStore) {
    await registerServiceKeyRoutes(app, { apiKeyStore: options.unifiedApiKeyStore });
  }
}
```

User management routes (`/api/admin/users/*`) were registered separately from `routes/users/` (not `routes/admin/`). There was no impersonation endpoint, no forced-logout, no lockdown mode, no banner/announcement API, no audit-log reader, no tenant freeze.

- Dependencies: none — this is a gap/absence claim, not an implementation quote.
- Tests that exercised this: `create-app-admin-auth.test.ts` exercises only service-key auth. Nothing else to port.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs` + `Program.cs:338-353`
- Contract/behavior: the C# admin surface also does not implement impersonation, forced logout, lockdown, or banner operations. It covers health (stub, finding 001), service key CRUD (findings 003-008), user CRUD (ported from `routes/users/`), and user invites/api-keys.
- Key code: see listing in `Program.cs:338-353`. Nothing new added beyond what TS had.
- Dependencies: n/a.
- Tests: `Tamma.Api.Tests/Admin/` (if present) covers only what's mapped.

## 3. The gap

Concrete behavioral difference — there is none. Both TS and C# lack these surfaces.

- TS did: nothing.
- C# does: nothing.
- For a caller sending `POST /api/admin/impersonate`, both return 404.
- In production with existing data / deployed clients, this means: the admin dashboard cannot impersonate tenants for support, cannot force users off a compromised session, cannot declare maintenance mode, cannot push an announcement banner to all clients. These are gaps vs. typical SaaS admin consoles, **not** regressions vs. the TS baseline.

Error paths: 404 in both.

## 4. Gap from stories

- Referenced story: none. `docs/stories/epic-16/16-3-admin-dashboard.md` alludes to dashboard UI but does not cover impersonation/lockdown/banners.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

This is documented as a finding so that when someone later says "why doesn't the admin dashboard have an impersonation button?" there's an artifact explaining that it never existed.

## 5. Status

- **Classification**: Not-yet-implemented — **but not a regression**. The port preserved parity with TS.
- **What's needed to finish**: nothing, unless we decide to build these features. If we do:
  1. Write a story per feature (impersonation, forced logout, lockdown, banners).
  2. Define audit event types: `ADMIN.IMPERSONATION.STARTED/ENDED`, `ADMIN.LOCKDOWN.ACTIVATED`, etc.
  3. Add DB tables: `admin_impersonation_sessions`, `maintenance_banners`.
  4. Build endpoints and dashboard UI.
- **Is it "just a stub" or is scope missing?** Scope was never specified. TS parity is preserved.
- **Blockers**: spec work.

## Remediation

- Files to modify: none (no action required).
- Files to create: new story files in `docs/stories/epic-16/` **only if** a product decision is made to build these.
- Tests to add: n/a.
- Estimated effort: 0h for this finding; 40-60h if features are scoped in.

## References

- TS source: `packages/api/src/routes/admin/` (commit `9e9a57c~1`) — three files, no more
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: none
- Related findings: none
- CLAUDE.md section: none
