# Finding 021: Invite token storage — TS stored raw; C# stores hash (OAuth callback lookup breaks)

**Scope**: auth
**Severity**: P1 (OAuth invite flow broken)
**Status**: Data-model drift (column rename + lookup semantics change)
**Estimated port effort**: 3h

## 1. What's in TS

Pre-delete snapshots at `git show 9e9a57c~1:packages/api/src/routes/users/invite-routes.ts` and archived SQL.

- Migration: `database/archived-sql-migrations/006_user_invites.sql` created the `user_invites` table with column `invite_token TEXT NOT NULL UNIQUE` — raw token.
- Schema comment line 2: *"The `invite_token` is stored as-is (hashed by app layer if desired) and looked up on OAuth callback."*
- Creation code:

```typescript
// packages/api/src/routes/users/invite-routes.ts:58-67 (9e9a57c~1)
const token = randomBytes(32).toString('base64url');
const expiresAt = new Date(Date.now() + INVITE_EXPIRY_MS).toISOString();

const invite = await inviteStore.createInvite({
  email,
  role: inviteRole as 'owner' | 'admin' | 'member',
  inviteToken: token,     // raw token → stored raw
  invitedBy: authUser.id,
  expiresAt,
});
```

- Lookup during OAuth callback:

```typescript
// packages/api/src/routes/auth/github-oauth.ts:148 (9e9a57c~1)
const invite = await inviteStore.getInviteByToken(inviteToken);
// inviteStore.getInviteByToken runs: SELECT * FROM user_invites WHERE invite_token = $1
```

- Semantics: the raw token goes into the `state.invite` OAuth parameter, comes back in the callback, is passed directly to the store, which matches on the raw column. One hash function per call isn't needed because the column holds the raw value.

Security tradeoff: if `user_invites` is breached, all unaccepted invite tokens are usable by the attacker. For invites with 72-hour TTL, this is bounded exposure, and the TS design accepted it in exchange for simpler lookup.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- Entity: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs:9` — `public string InviteTokenHash { get; set; } = null!;` (note the `Hash` suffix and column rename).
- Creation code:

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:116-128
var token = Guid.NewGuid().ToString("N");
var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
var userId = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

var invite = await inviteRepo.CreateAsync(new UserInvite
{
    TenantId = tenantContext.TenantId.Value,
    Email = req.Email,
    Role = req.Role,
    InviteTokenHash = tokenHash,     // stored hashed
    InvitedBy = userId is not null ? Guid.Parse(userId) : Guid.Empty,
    ExpiresAt = DateTime.UtcNow.AddDays(7)
});

return Results.Created($"/api/admin/users/invites/{invite.Id}",
    new { id = invite.Id, token, expiresAt = invite.ExpiresAt });  // returns raw to caller
```

- Lookup (doesn't exist). Nowhere in `IInviteRepository` or its implementation is there a `GetByTokenHashAsync` or `GetByTokenAsync`. Searching the repo file:

```csharp
// apps/tamma-elsa/src/Tamma.Data/Repositories/IInviteRepository.cs (inferred from AdminEndpoints callsites)
// Has: CreateAsync, ListPendingByTenantAsync, DeleteAsync
// Missing: any lookup-by-token
```

## 3. The gap

Two connected regressions:

1. **Wrong lookup key**: The column became `InviteTokenHash`. If the OAuth callback (Finding 008 — currently stubbed) were implemented, it would receive the raw token from `state.invite` and need to hash it before lookup. Specifically: `var tokenHash = SHA256(rawToken)`; `await inviteRepo.GetByTokenHashAsync(tokenHash)`. That repo method does NOT exist. So even after Finding 008 is fixed, the invite-via-OAuth flow remains broken until this gap is closed.

2. **Missing repository method**: There is no `IInviteRepository.GetByTokenHashAsync`. The only lookup methods are by tenant (`ListPendingByTenantAsync`) and by ID (`DeleteAsync`). The OAuth callback cannot find an invite by the token a user is presenting.

Production scenario:
1. Admin invites `bob@company.com` as `admin` → invite stored with `InviteTokenHash = SHA256(raw)`; response returns `raw` and `invite_link = /invite/<raw>`.
2. Bob clicks the link → redirected to `/api/auth/github?invite=<raw>` → GitHub OAuth → callback lands with `state.invite=<raw>`.
3. Callback (once Finding 008 is fixed): hashes the raw token → queries `user_invites` WHERE `InviteTokenHash` = `<hashed>` — **but there is no such repo method**. Cannot succeed.
4. Alternative if wrong column semantics: if the callback were to query directly by `raw` against `InviteTokenHash`, no match (hash ≠ raw). Invite remains pending; Bob gets a `member` role instead of the invited `admin`.

Additional subtle issue: `UserInvite.InviteTokenHash` uses SHA-256. Finding 003 and 001 argued for scrypt for passwords and (preserved) scrypt for API keys. Here we have SHA-256 for a short-lived invite token — defensible, since the token has 128 bits of entropy from `Guid.NewGuid()` (only 122 usable bits — Guid v4 reserves 6) and short TTL.

Error paths:
- TS: invite lookup miss → OAuth callback treats user as uninvited → assigns `member` role → user signs in with reduced privileges.
- C#: repo method missing → can't even attempt lookup.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md` (covers invite flow).
- Story §82-88 describes the invite token as a `randomBytes(32).toString('base64url')` passed through OAuth state and looked up directly in the store. The impl plan does not prescribe raw-vs-hashed storage.
- Implicitly the TS implementation chose raw (for simpler lookup); the C# implementation chose hashed (for at-rest breach resilience). Both are defensible.
- Story alignment:
  - [x] Matches TS behavior (raw storage, direct lookup)
  - [ ] Matches C# behavior (hashed storage; lookup-method missing)
  - [ ] Describes a third behavior
  - [x] No story — the decision wasn't written

## 5. Status

- **Classification**: Data-model drift (column shape changed) + Incomplete (the lookup side of the shape change was never wired up).
- **What's needed to finish**:
  1. Add `Task<UserInvite?> GetByTokenHashAsync(string tokenHash)` to `IInviteRepository`.
  2. Implement in `InviteRepository` via EF: `await db.UserInvites.FirstOrDefaultAsync(i => i.InviteTokenHash == tokenHash && i.AcceptedAt == null)`.
  3. In the OAuth callback (coordinates with Finding 008), hash the incoming `state.invite` → call `GetByTokenHashAsync`.
  4. Add `Task MarkAcceptedAsync(Guid inviteId)` — currently no "accept" method exists either.
  5. Update `docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md` to codify hashed-storage decision.
- **Is it "just a stub" or is scope missing?** Scope missing — the hashed schema was created, but nothing ever performs the hash-and-lookup on the read side.
- **Blockers**: Finding 008 (OAuth callback needed to exercise this flow).

## Remediation

- Files to modify: `apps/tamma-elsa/src/Tamma.Data/Repositories/IInviteRepository.cs`, `InviteRepository.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` (GitHubCallback — coordinates with Finding 008).
- Files to create: None.
- Tests to add:
  - `InviteRepositoryTests.GetByTokenHashAsync_ValidPendingInvite_ReturnsInvite`.
  - `InviteRepositoryTests.GetByTokenHashAsync_AlreadyAccepted_ReturnsNull`.
  - `InviteRepositoryTests.GetByTokenHashAsync_Expired_StillReturns_ButCallerShouldCheckExpiresAt`.
  - `InviteRepositoryTests.MarkAcceptedAsync_SetsAcceptedAt`.
  - `GitHubCallback_WithValidInvite_AssignsInviteRole` (Finding 008 ties here).
- Estimated effort: 3h
  - Repo additions: 1h
  - Callback integration: 1.5h (blended with 008)
  - Tests: 1h (bulk)

## References

- TS source: `packages/api/src/routes/users/invite-routes.ts:58-67`, `packages/api/src/routes/auth/github-oauth.ts:148-161` (commit `9e9a57c~1`)
- Archived SQL: `database/archived-sql-migrations/006_user_invites.sql`
- C# source: `apps/tamma-elsa/src/Tamma.Data/Entities/UserInvite.cs:9`, `Endpoints/AdminEndpoints.cs:116-131`, `Repositories/IInviteRepository.cs` (missing method)
- Story: `docs/stories/epic-18/18-3-organization-tenant-creation-impl-plan.md` (§82-88)
- Related findings: `008-oauth-callback-stub.md` (read side consumer)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Fixed
- **Commit**: `e56b04d`
- **Notes**: GitHubCallback hashes state.invite via SHA-256 then calls IInviteRepository.GetByTokenHashAsync (already exists). Invite role applied via tenant_memberships; AcceptAsync marks the invite consumed.
