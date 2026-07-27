# Implementation Plan — Story 46-1: Persisted Model Selection

## Scope & Deliverable

When this story is done, a model chosen through `PUT /api/admin/providers/{key}/settings` or
`PUT /api/v1/agents/providers/{provider}/model` is stored in the control-plane `provider_settings`
table and honoured by every default-model consumer — the inline tool-loop runner, `ManagedAgent`'s
fallback, null-model chain entries, and `LlmProxyService` — under the precedence
**tenant override → platform DB → config → descriptor**, with a 60-second worst-case propagation
bound across API instances and zero behaviour change for installs that never write a row. The
shipped default-model strings have been verified against live provider lists once and refreshed
where rotten.

## Pre-Reading

- `docs/stories/epic-46/README.md` — ownership ladder, D2/D3/D3a/D3b
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:1088-1201` — the config
  resolution being rewired, including the early return at `:1119-1127` and the
  `Anthropic:Model` case at `:1141-1143`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IInlineToolLoopRunner.cs:90-98` +
  `ManagedAgent.cs:908-935` — the sync callers that force the snapshot design
- `apps/tamma-elsa/src/Tamma.Api/Services/SaaS/LlmProxyService.cs:29-98` — the const being retired
  to last-resort
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:300-330` — nullable
  `ProviderHandle.Model`; the audit target
- `Tamma.Data/Entities/AgentRoleSelection.cs:10-23` — the documented XOR-principal pattern (note
  its residency differs; epic D3a says why this table is CP-resident)
- `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` — recent migration naming
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ProviderCredentialEndpoints.cs` — the tenant-surface
  conventions (NormalizeProvider, 404-not-enumerate, member 403, audit emission at `:285-316`)
- `apps/tamma-elsa/src/Tamma.Core/Audit/SensitiveActionCatalog.cs:78,205` — catalog entry pattern
- `apps/tamma-elsa/src/Tamma.ElsaServer/appsettings.json:64-89` — config examples to keep truthful

## Design Decisions

- **D1 — One table, three row kinds, one unique index.** Platform rows
  (`TenantId NULL, UserId NULL`), tenant rows, user rows —
  `UNIQUE NULLS NOT DISTINCT (TenantId, UserId, ProviderKey)` gives at most one row per principal
  per provider, including the all-null platform principal. The `Scope` column is derivable but
  stored anyway for query legibility and index-only reads; a CHECK ties it to the null pattern so
  it cannot lie.

- **D2 — Snapshot store, not per-call queries.** Forced by the sync callers (story context). The
  snapshot is a single `volatile` immutable dictionary
  `(providerKey, principal) → (model, enabled)` rebuilt whole on refresh — no per-entry locking,
  readers never block. Writes go DB-first, then rebuild synchronously before returning, so the
  writing instance is consistent immediately; other instances converge within TTL. Do NOT use
  `IMemoryCache` — the whole-snapshot swap is simpler to reason about and to test than eviction
  semantics.

- **D3 — The tenant leg keys by the principal the request actually has.** SaaS requests carry
  `TenantId`; single-user requests carry a user. Rather than teach the egress path a new principal
  type, `TryGetModel(providerKey, tenantId)` handles the SaaS leg, and the single-user leg's
  user-keyed row is resolved by the store internally when `ITammaModeProvider` reports single-user
  mode (the store looks up the sole user's row under `tenantId == null`). This keeps the
  runner/proxy call sites to one optional-Guid parameter — the same shape as
  `IProviderCredentialResolver.ResolveAsync`.

- **D4 — `LoadProviderConfig` gets restructured, not patched.** The current shape (early return on
  config-section-exists) cannot express "DB wins over config". Restructure to: resolve
  `BaseUrl`/`Timeout` exactly as today; resolve `DefaultModel` through a single private method
  `ResolveDefaultModel(canonicalKey, tenantId?)` implementing the four steps, called from both the
  section and the descriptor branches. The method is also what the endpoints use to report
  `source` provenance — one implementation, two consumers, no restatement (the 43-1 lesson).

- **D5 — Chain-entry audit is in-scope, bounded.** Enumerate consumers of `ProviderHandle.Model`
  (grep `\.Model` within chain-consuming services); for each null-model path, confirm it funnels
  into `GetDefaultModel`/`LoadProviderConfig`. Fix any that reads config or a constant directly.
  Record the audited list in the PR description. If an unexpected consumer turns out to be large,
  it becomes a named follow-up — do not silently expand this story.

- **D6 — The defaults refresh changes data, not behaviour shape.** AC7's edits are string value
  changes + their pinned tests. Anything that looks like it needs logic changes during the refresh
  (e.g. a provider rejecting its own descriptor default in a way fail-soft can't mask) gets filed,
  not fixed inline.

## Implementation Steps

1. **Entity + `ControlPlaneDbContext` registration + migration** (`AddProviderSettings`), with the
   XOR CHECK, scope CHECK, and unique index. Round-trip test via the existing Testcontainers
   migration-test pattern.
2. **`IProviderSettingsStore` / `ProviderSettingsStore`** with snapshot semantics (D2, D3);
   register singleton; unit tests for read/write/invalidate/TTL/mode-aware principal resolution.
3. **`ResolveDefaultModel`** inside `InlineToolLoopRunner` (D4) + the
   `GetDefaultModel(provider, tenantId?)` overload on `IInlineToolLoopRunner` +
   `ManagedAgent.cs:929` call-site update. Precedence matrix tests.
4. **`LlmProxyService`** — store consultation before the const (AC4); test.
5. **Chain audit** (D5) — audit, fix, record.
6. **Endpoints** — platform settings PUT/DELETE on `ProviderAdminEndpoints.cs`; tenant roster
   (`GET /api/v1/agents/providers/models` — enabled providers only, BYOK-presence metadata via
   the `ListProviders` cabinet query) + tenant model GET/PUT/DELETE beside the BYOK routes;
   `source`/`enabled` additions to the 46-0 status list;
   pricing-known warning via `IProviderPricingService`; RBAC per the epic table; validation;
   404-not-enumerate.
7. **Audit catalog entry + emission** — `SensitiveActionCatalog` addition; emit on every mutation
   (follow `EmitByokChangeAsync`, `ProviderCredentialEndpoints.cs:285-316`).
8. **Defaults refresh (AC7)** — verify, refresh, update appsettings examples + pinned tests,
   record evidence.
9. **Full test pass** — precedence matrix, RBAC, staleness bound documented-not-asserted (the TTL
   value is pinned by one test so a silent change to the bound shows up).

## Data & Migrations

One ControlPlane migration: `provider_settings` (columns per AC1; indexes: the unique principal
index + `(ProviderKey)` for platform-row lookups). No tenant-schema migration — D3a.

## Events

`PROVIDER.SETTINGS_CHANGED.SUCCESS` per mutation (AC8): tags
`{provider, scope, operation, mode}`, data `{previousModel?, model?, enabled?}`. Emitted via
`ISensitiveActionEmitter`; never key material. Read paths emit nothing new.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | Precedence matrix (16 cases) | tenant > platform-DB > config > descriptor, each fallback correct |
| 2 | Config-section + platform-row | DB wins (the early-return regression) |
| 3 | `Anthropic:Model` legacy case | still honoured at the config step, still anthropic-only |
| 4 | Empty-string config model | treated as no-opinion (today's behaviour) |
| 5 | Store write → same-instance read | immediate |
| 6 | Store TTL | expired snapshot refetches; TTL value pinned |
| 7 | Single-user principal | user-keyed row resolves with `tenantId: null` |
| 8 | `LlmProxyService` | request-model > store > const |
| 9 | `ManagedAgent` fallback | tenant-aware overload used |
| 10 | Endpoint RBAC | member 403 on writes (both surfaces); platform routes owner-only; reads per table |
| 11 | Validation | empty/oversized/control-char model → 400 |
| 12 | Unknown/alias keys | alias → canonical row; unknown → 404 |
| 13 | Pricing warning | unknown `(provider, model)` → `pricingKnown:false` + warning |
| 14 | Audit emission | one event per mutation, correct tags, no key material |
| 15 | Migration round-trip | up/down clean; XOR + unique constraints enforced by Postgres |
| 16 | No-row install | byte-identical resolution to pre-story behaviour (golden comparison against `LoadProviderConfig` outputs for all 15 keys) |

## Definition of Done

- All ACs met; tests 1–16 green; `dotnet test` green overall.
- AC7 evidence in the PR (per-provider verification outcome, including "unchanged" rows).
- `grep -rn "claude-sonnet-4.5\|claude-opus-4.7\|claude-haiku-3.5" apps/tamma-elsa/src` returns
  only strings verified valid or updated (the dot-formed suspects resolved one way or the other).
- The multi-instance 60 s bound stated in `IProviderSettingsStore` XML docs.
- No change to chain-entry semantics: a chain entry naming a model still wins (test 1's matrix
  includes a named-model case proving the resolver is never consulted).

## Dependencies & Sequencing

- **Blocked by:** 46-0 by preference (shared endpoints file); not hard.
- **Blocks:** 46-2, 46-3.
- **Shared-edit register:** `ProviderAdminEndpoints.cs` (46-0 creates, this extends);
  `ProviderCredentialEndpoints.cs` (46-0 adds the tenant models route, this adds the tenant model
  settings routes — disjoint methods, same file).

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Restructuring `LoadProviderConfig` breaks a legacy path | test 16's golden comparison over all 15 keys, plus the existing runner tests; the method's contract (BaseUrl/Timeout untouched) is stated in the story |
| The sync-snapshot design hides a write for up to 60 s on other instances | documented bound; TTL pinned by test; matches the BYOK cache posture users already live with |
| A rotten default is "fixed" to another wrong slug | AC7 requires live-list or curl evidence per change, in the PR |
| The chain audit (D5) finds a large bypass | bounded by decision: file a follow-up, do not absorb |
| `Enabled` flag creates the impression disable is enforced on egress | Out of Scope states it plainly; the endpoints reject writes against disabled providers and the UIs grey them out, which is the user-visible contract for now |

## Effort Breakdown

| Task | Days |
|---|---|
| Entity, migration, store + tests | 1.0 |
| `LoadProviderConfig` restructure + overloads + proxy + matrix tests | 1.0 |
| Endpoints (platform + tenant) + RBAC + validation + audit + tests | 1.25 |
| Chain audit (D5) | 0.25 |
| Defaults refresh (AC7) + evidence | 0.5 |
| **Total** | **4.0** |
