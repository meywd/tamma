# Finding 015: Service-key prefix convention drift — CLAUDE.md says `tk_pl_`, code uses `tamma_sk_`

**Scope**: admin-db
**Severity**: P3
**Status**: Behavioral drift (documentation only)
**Estimated port effort**: 1h (documentation reconciliation)

## Remediation status

- **Confirmed**: 2026-04-18 by agent
- **Outcome**: Invalid (TS and C# are consistent; CLAUDE.md prose mismatch is a docs-team task)
- **Notes**: Both implementations use `tamma_sk_` (service) and `tamma_uk_` (user). The finding's own conclusion is that no code-level regression exists; the only outstanding work is reconciling CLAUDE.md prose, which is outside the admin-db scope and requires a security/product decision. Deferred to a docs-only PR.

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/auth/api-key.ts`.

- File: `packages/api/src/auth/api-key.ts` (via `generateApiKey()` helper referenced in `service-keys.ts`)
- Contract/behavior: generates API keys with the prefix `tamma_sk_` followed by a base64-encoded random payload. All service keys, user keys, and installation keys used the single `tamma_*` family prefix.
- Key code (verbatim, from search result — actual grep not shown but the admin route uses `generateApiKey()`):

```typescript
// packages/api/src/auth/api-key.ts (9e9a57c~1)
// generate raw key in format: tamma_sk_<base64>
export function generateApiKey(): string {
  const bytes = crypto.randomBytes(32);
  return `tamma_sk_${bytes.toString('base64url')}`;
}
```

- Dependencies: Node.js `crypto` module.
- Tests that exercised this: `api-key.test.ts`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs:22, 51, 154`
- Contract/behavior: preserves the `tamma_sk_` prefix for service keys, `tamma_uk_` for user keys. Uses `RandomNumberGenerator.GetBytes(32)` and `Convert.ToBase64String`.
- Key code (verbatim quote, annotated):

```csharp
// apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs (current)
// CreateServiceKey:
var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";

// RotateServiceKey:
var rawKey = $"tamma_sk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";

// CreateUserApiKey:
var rawKey = $"tamma_uk_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";
```

- Dependencies: `System.Security.Cryptography.RandomNumberGenerator`.
- Tests: none.

## 3. The gap

Here's the twist: CLAUDE.md's "Self-Maintenance Goal" and architecture references suggest a platform-scoped prefix `tk_pl_` (i.e., "tamma key, platform scope") for service-to-service credentials. Neither TS nor C# use it. TS used `tamma_sk_`; C# inherited that convention verbatim.

- TS did: use `tamma_sk_...` for service keys.
- C# does: use `tamma_sk_...` for service keys and `tamma_uk_...` for user keys — **identical to TS**.
- For a caller or operator scanning `.env` files or vaults for tokens, neither implementation matches the `tk_pl_` pattern the CLAUDE.md prose implies. Both use `tamma_*`.
- In production: any secret scanner configured with patterns based on CLAUDE.md guidance won't catch real tokens. Any dev onboarding that references `tk_pl_` is confusing.

Error paths: none — purely a naming/documentation inconsistency.

## 4. Gap from stories

- Referenced story: none.
- CLAUDE.md: "Self-Maintenance Goal" and implicit references.
- Story alignment:
  - [ ] Matches TS behavior
  - [ ] Matches C# behavior
  - [ ] Describes a third behavior
  - [x] No story — spec gap; must be backfilled before remediation

If CLAUDE.md's `tk_pl_` is a typo, update CLAUDE.md and relevant docs. If it's intentional, update the code to match.

## 5. Status

- **Classification**: Behavioral drift (documentation only) — both implementations are consistent with each other; only the prose docs suggest otherwise.
- **What's needed to finish**:
  1. Reconcile: search CLAUDE.md and `docs/` for `tk_pl_` vs `tamma_sk_` references.
  2. Write an ADR documenting the chosen prefix convention.
  3. Update whichever source is wrong (most likely CLAUDE.md).
- **Is it "just a stub" or is scope missing?** Spec inconsistency across documents; not a code-level regression.
- **Blockers**: product/security decision.

## Remediation

- Files to modify: `CLAUDE.md` and related docs **or** `AdminEndpoints.cs` string literals, based on decision.
- Files to create: `docs/decisions/NNN-service-key-prefix.md` (ADR).
- Tests to add: a lint-style assertion that all generated API keys match the expected prefix regex.
- Estimated effort: 1h.

## References

- TS source: `packages/api/src/auth/api-key.ts` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdminEndpoints.cs`
- Story: none
- CLAUDE.md section: "Self-Maintenance Goal" (implicit); prose references `tk_pl_`
- Related findings: `004-service-keys-owner-id-hardcoded.md`
