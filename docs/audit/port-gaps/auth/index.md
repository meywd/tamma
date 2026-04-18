# Auth Port-Gap Findings (Epic 19 TS → C# migration)

Audit root: `/home/meywd/tamma/docs/audit/port-gaps/auth/`
Source commit (pre-delete): `9e9a57c~1` (TS `packages/api/`)
Target: `apps/tamma-elsa/src/Tamma.Api/` on `feat/auth-foundation`

Severity legend:
- **P0** — cutover-blocking; existing persisted data or deployed clients break on day 1
- **P1** — feature broken but not data-destroying
- **P2** — correctness / hardening regression
- **P3** — contract drift / low-impact

| # | File | Sev | Summary |
|---|------|-----|---------|
| 001 | `001-password-hash-scrypt-vs-argon2.md` | P0 | TS stores `scrypt:N:r:p:...` hashes; C# stores `$argon2id$...`. `VerifyPassword` rejects the other format outright. Every existing email+password user is locked out. |
| 002 | `002-jwt-claim-shape.md` | P0 | TS signs `{sub, tenantId, role, platformRole, email, name, authMethod}`. C# signs `{sub, tid, role, email, jti, iat}`. Claim-name rename + drop of `platformRole`/`name`/`authMethod` invalidates every live session. |
| 003 | `003-api-key-hash-algorithm.md` | P0 | TS hashes API keys with scrypt (hex). C# hashes with SHA-256 (hex). Every row in `api_keys.key_hash` is unverifiable after cutover. |
| 004 | `004-session-cookie-payload-and-domain.md` | P0 | TS cookie `tamma_session` = access JWT, 900s, `Domain=.tamma.dev`. C# cookie = raw refresh token, 7d, no domain. Breaks cross-subdomain auth, nginx `role-check`, `/api/auth/me`. |
| 005 | `005-email-verification-stub.md` | P0 | C# `VerifyEmail` endpoint computes a token hash and returns 200 without any DB lookup or mutation. TS looked up by token-hash, expiry-checked, set `emailVerified=true`. Users never become verified. |
| 006 | `006-login-email-verified-check-missing.md` | P0 | TS login returns 403 if `emailVerified=false`. C# login never reads the flag — unverified users can log in. Violates AC 2 of Story 18-2. |
| 007 | `007-refresh-token-rotation-broken.md` | P0 | C# `Refresh` issues a new access token but does NOT revoke the old refresh token, does NOT issue a new one, has no reuse-detection family revoke. Stolen refresh good for 7 days. |
| 008 | `008-oauth-callback-stub.md` | P0 | `GitHubCallback` returns `{ message: "GitHub callback - not yet implemented" }`. TS implementation exchanged code→token, fetched profile, processed invite state, upserted user, auto-linked installs, issued JWT, redirected. |
| 009 | `009-oauth-state-csrf-missing.md` | P0 | C# `GitHubAuth` builds authorize URL without a `state` query parameter. TS encoded `{rd, invite}` JSON as base64url state. Without state, OAuth flow is CSRF-vulnerable and cannot carry invite/rd metadata. |
| 010 | `010-role-check-service-to-permission-map.md` | P0 | TS mapped `?service=elsa\|logs\|admin` to `elsa:access / logs:access / admin:access` permissions. C# ignores `service` query param entirely and returns all permissions for the role. nginx gateway gating is broken. |
| 011 | `011-get-me-reads-bearer-not-cookie.md` | P1 | TS `/api/auth/me` verified JWT from `tamma_session` cookie (unified nav on every subdomain). C# uses JWT Bearer auth scheme — reads `Authorization: Bearer` header only. Unified nav fetch fails with 401. Response shape also changed. |
| 012 | `012-login-timing-oracle.md` | P3 | TS hashes a dummy `scrypt:...:deadbeef:deadbeef` when the user is not found so the work factor is equal to the real path. C# returns immediately on null user — timing reveals account existence. |
| 013 | `013-password-strength-validation-missing.md` | P2 | TS `validatePasswordStrength` checks length, upper, lower, digit, common-password list. C# register enforces only `Length < 8`. Password-reset/confirm has no strength check at all. |
| 014 | `014-no-rate-limit-on-resend-and-reset.md` | P2 | TS tracks in-process per-email timestamps and 429s after 3/hour on `resend-verification` and `password-reset/request`. C# has neither. |
| 015 | `015-password-reset-sends-to-github-only-users.md` | P2 | TS skips sending reset email if `user.authMethod === 'github'`. C# sends regardless — flips the user's `auth_method` silently when the new password is set (and lets them bypass GitHub). |
| 016 | `016-require-self-or-role-missing.md` | P2 | TS `requireSelfOrRole('admin')` let users list / create / delete their own API keys. C# wires all `/api/admin/users/{id}/keys/*` routes behind `ApiKeysManage` which requires `admin` or `owner`. Regular members cannot manage their own keys. |
| 017 | `017-login-lockout-stale-clear-gap.md` | P3 | TS `recordFailedAttempt` clears an expired lockout before counting the new attempt. C# appends first, resets later in `IsLocked`. If a locked-expired account has a failed attempt arrive first, the attempt is counted against the dead-lockout window. |
| 018 | `018-admin-update-user-role-missing-guards.md` | P1 | TS blocked self-role-change, required `owner` to promote to admin/owner, verified target existed. C# only verifies target exists. Any `owner` can demote themself; any `admin` can promote any user to `owner` (endpoint is gated OwnerAccess at the route, but no self-protection). |
| 019 | `019-admin-delete-user-no-cascade.md` | P1 | TS soft-deletes + revokes all user API keys + unlinks all user→installation rows. C# only soft-deletes. Keys remain valid, installations remain linked. |
| 020 | `020-admin-create-user-api-key-format.md` | P2 | TS generates `tamma_sk_<base64url>` hashed with scrypt. C# generates `tamma_uk_<base64>` hashed with SHA-256. Different prefix, different charset, different hash. Not interchangeable with any other key flow. |
| 021 | `021-invite-token-raw-vs-hash.md` | P1 | Archived SQL `006_user_invites.sql` stored raw `invite_token` unique-indexed for O(1) lookup-by-token on OAuth callback. C# `InviteTokenHash` column (SHA-256) means OAuth callback cannot find an invite by presented token (it never hashes the state.invite to look it up in current C# code). |
| 022 | `022-user-repository-missing-methods.md` | P1 | `IUserRepository` is missing `SetEmailVerifiedAsync`, `UpdateAuthMethodAsync`, `SetGitHubIdAsync`, `UnlinkAllInstallationsAsync`, `GetUserInstallationsAsync`, `LinkUserToInstallationAsync`, `GetUserSettingsAsync`, `UpdateUserSettingsAsync`, `UpdateVerificationTokenAsync` (all present on TS `IUserStore`). |
| 023 | `023-user-installations-table-absent.md` | P1 | TS had `user_installations(user_id, installation_id, role)` table. C# schema has no `UserInstallations` table and no navigation on `User`. OAuth callback's "auto-link to all active installations" path has no destination. |
| 024 | `024-user-api-keys-legacy-table-orphan.md` | P2 | TS had `user_api_keys` (migration 005) that was consolidated into `api_keys` by migration 009. C# schema does not include the legacy table and does not re-run the consolidation copy. Any legacy rows left in prod are orphaned without a migration path. |
| 025 | `025-user-settings-jsonb-column-missing.md` | P1 | TS `users.settings jsonb` (migration 004) was the SaaS-mode home for per-user provider config (equivalent of `~/.tamma/providers.json`). C# `User` entity + InitialSchema has no `Settings` column. User-level provider config has nowhere to live. |
| 026 | `026-users-email-not-null-regression.md` | P2 | TS allowed `email NULL` (GitHub users without public email). C# declared `Email` as NOT NULL `varchar(255)`. GitHub users without an email claim cannot persist. |
| 027 | `027-users-github-id-narrowed-bigint-to-int.md` | P2 | TS `github_id BIGINT`. C# entity `int? GitHubId` mapped to `integer`. GitHub user IDs above 2^31 (2.1B) overflow. |
| 028 | `028-case-insensitive-email-index-missing.md` | P2 | TS migration 018 created `idx_users_email_lower ON users (LOWER(email)) WHERE email IS NOT NULL`. C# has plain `IX_users_Email` on raw `Email` column — case-sensitive uniqueness. |
| 029 | `029-unified-auth-missing.md` | P1 | TS `authenticateApiKey` middleware populated `request.authPrincipal` with a scope-aware tagged union, enforced `X-Tenant-Id` on service keys, warned on rotation-grace keys, emitted structured per-request audit log. C# `ApiKeyAuthHandler` does none of these. |
| 030 | `030-auth-principal-union-absent.md` | P1 | TS `AuthPrincipal` tagged union distinguished `user` / `installation` / `service` scope with scope-specific fields (role, installationId, permissions). C# has no analog — everything lives in flat `ClaimsPrincipal` claims. Downstream code cannot branch on scope typesafely. |

**Total**: 30 findings. 10 P0, 9 P1, 10 P2, 3 P3 (noting that `018-admin-update-user-role-missing-guards` is P1 because multiple permission guards are absent, not a simple drift).

## Inputs I could not locate

- None — every finding here has both a TS source reference (extracted via `git show 9e9a57c~1:...`) and a current C# source, plus either a story file or a CLAUDE.md/architecture.md governing section. Where a story did not specifically cover a finding (e.g. scrypt-vs-argon2 algorithm choice is NOT in any story — story 18-1 *says* argon2), the finding notes that story 18-1 matches the C# implementation and the TS was ahead of spec on the algorithm but the wire format is what breaks.

## Findings combined

- No findings were merged. All 30 items are orthogonal.
