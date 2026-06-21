# Story 32-19 — Agent Style/Voice Variants (`AgentStyleVariant`, the 32-12 overlay split-out)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Re-home the still-valuable tone/voice/verbosity idea from the OLD 32-12 (`atlas`/`nova`
"review voice") as a **separate, optional `AgentStyleVariant`** — explicitly **NOT a persona**
(persona = the named system agent, 32-15) and **NOT a custom agent** (= own prompts, 32-17). A
variant is a small style descriptor (tone/verbosity knobs and/or a short style-prompt fragment) that
the call-LLM endpoint (32-5) merges **on top of** the resolved base prompt — **additively, after**
Epic-27/custom-prompt resolution (a new step 4b), never replacing it, never empty-fallback. It changes
**no** provider/model/credential. A run applies **zero or one** variant; default = none = no behaviour
change. Binding is per-`(principal, role)`, constrained to the principal's **enabled** variants, with
the same visibility/XOR/unique-nulls-not-distinct discipline as `prompt_overrides` /
`AgentRoleSelection` / `TenantAgentEnablement`.

**Story file:** `docs/stories/epic-32/story-32-19/32-19-agent-style-voice-variants.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.4 the
split — variant ≠ persona; §3.0 reframe; §2.6 the composition step 4 this overlay rides on)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + data `Tamma.Data`).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). **`packages/api` is DELETED — there is no TypeScript path; all C#.**

---

## Non-goals (YAGNI guard)

- **NO change to provider/model/credential/cost.** A variant is a voice overlay only. The entity
  carries no `provider`/`model`/key/base-prompt fields; `credentialSource` (32-3) is untouched; the
  cost path (34-11/34-5) is untouched. If your change makes `providerUsed`/`modelUsed`/
  `credentialSource` differ with vs without a variant, the design is wrong — stop.
- **NO replacing/blanking the base prompt.** `ComposeOverlay` is a **pure additive suffix**. The base
  keeps its tenant→system→**error** contract (owned by 32-5 step 4). The variant never rescues, blanks,
  or short-circuits an empty base (`feedback_resolution_no_empty_fallback`).
- **NO persona/custom-agent semantics.** This is NOT a persona and NOT a custom agent. No
  `/api/personas` route, no provider preset, no own-prompt editing. Distinct route
  (`/api/style-variants`), distinct event family (`AGENT.STYLE_VARIANT.*`).
- **NO edit to the 32-5 call-LLM composition here.** This story ships `IAgentStyleVariantService`;
  32-5 (or a tiny amendment to it) inserts step 4b. Code to the interface; do not touch
  `ManagedAgent.RunAsync`.
- **NO variant axis on benchmarking.** 32-12/32-10 key benchmarking on the persona-agent identity. A
  variant is an orthogonal dimension; adding it to the harness is a future story, out of scope.
- **NO per-user layer in SaaS.** Variants + bindings are per-tenant (members read, can't write),
  mirroring "no per-user override layer in SaaS." Single-user keys by `UserId`.

---

## Current-state findings (verify at implementation time, repo @ the worktree HEAD)

| Seam | Where it is today | How 32-19 uses it |
|---|---|---|
| **Principal-XOR + index discipline** | `Tamma.Data/Entities/AgentRoleSelection.cs` + `TenantAgentEnablement` (32-16) configured in `TammaModelConfiguration.cs` (XOR CHECK + `UNIQUE NULLS NOT DISTINCT`). | Mirror **exactly** for `AgentStyleVariant` (per-principal name uniqueness) and `AgentStyleVariantSelection` (one variant per `(principal, role)`). |
| **Catalog membership** | `TenantAgentEnablement` + `ITenantAgentEnablementService` (32-16): `Enabled` flag, own-private implicit-enabled, per-tenant, `DefaultPersonaName` seeded. | Reuse the pattern for variant enablement (`SetEnabledAsync`, implicit-private, disable-own ⇒ 409). |
| **Per-(principal, role) binding** | `AgentRoleSelection` (32-2): one agent bound per role per principal. | Mirror for `AgentStyleVariantSelection`: at most one variant per role per principal; `variantId:null` clears. |
| **Base prompt render** | 32-5 `ManagedAgent.RunAsync` step 4: Epic 27 `(principal, role, action)` for personas / custom agent's own prompts; tenant→system→**error**. | The overlay rides **on top of** the rendered system prompt at a new step 4b; never edits step 4. |
| **CP placement** | `ControlPlaneDbContext` + `ControlPlaneDbContextModelTests` strict `BeEquivalentTo` list + `Program.cs` "Wiping Tamma-managed public-schema tables" DROP list. | Both new tables are CP-resident → add 2 DbSets, 2 strict-list entries, 2 DROP-list entries. NOT `EfTenantDbMigrator`. |
| **DCB events** | `IEventRepository.AppendAsync`, tenant-scoped; `AGENT.ENABLED/DISABLED.SUCCESS` family (32-16). | Emit `AGENT.STYLE_VARIANT.*`; tenant-scope carries `TenantId`, single-user carries `userId`. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser \| SaaS), process-stable. | Derive principal (`TenantId` in SaaS / `UserId` in single-user) for every read/write. |
| **RBAC policies** | 32-2/32-16 `/api/agents` group: `AgentManage` (owner/admin), member read; `PlatformOwnerAccess` for platform catalog. | Reuse for `/api/style-variants`: writes owner/admin, reads member, shipped catalog `PlatformOwnerAccess`. |

**Key insight:** the only genuinely new code is two CP entities, the `IAgentStyleVariantService`
(CRUD/enable/bind + the resolve/compose primitives), the **pure `ComposeOverlay`** function, the
seeder, and the route group. Everything else is mirroring the 32-16/32-2 discipline. The 32-5 wiring
is a one-step (4b) insertion owned by 32-5, behind this story's interface.

---

## Architecture

```
StyleVariantEndpoints (/api/style-variants)           -- CRUD + enablement + per-role bindings
        |
        v
IAgentStyleVariantService                             -- owns the entities + the primitives:
  CreateAsync/UpdateAsync/DeleteAsync (own-private)      -- public shipped variants read-only
  SetEnabledAsync(id, bool)                              -- catalog membership (per-tenant, 32-16 pattern)
  BindAsync(role, variantId?)                            -- one variant per (principal, role); null clears
  ResolveActiveVariantAsync(role) -> ResolvedStyleVariant? -- the primitive 32-5 calls at step 4b
  ComposeOverlay(baseSystemPrompt, variant?) -> string     -- PURE, additive suffix (base is a prefix)

32-5 ManagedAgent.RunAsync (NOT edited here; wired via the interface):
  4.  prompt = render base (Epic 27 / custom)  -- tenant->system->ERROR
  4b. variant = ResolveActiveVariantAsync(role)            -- null = no overlay (default)
      systemPrompt = ComposeOverlay(prompt.System, variant) -- additive; base verbatim prefix
  5.  emit AGENT.RUN.STARTED { ..., styleVariantId? }       -- optional tag (event owned by 32-5)
  6.  loop with systemPrompt                                -- provider/model/credential/cost UNCHANGED
```

Per-mode ownership (CLAUDE.md two-scoping-model rule): single-user = `UserId`-keyed CP rows, the sole
user manages; SaaS = `TenantId`-keyed CP rows, owner/admin manage, members read-only. Default in both
modes = **no overlay**. A variant never changes provider/model/credential. Mode from
`ITammaModeProvider`.

---

## Task breakdown

Order: T1 (entities + EF + migration + CP wiring) → T2 (service CRUD/enable/bind + events) →
T3 (`ResolveActiveVariantAsync` + the pure `ComposeOverlay`) → T4 (endpoints + RBAC + DI) →
T5 (seeder) → T6 (orthogonality + provider-unchanged + isolation + mode/CP tests). T2 and T3 both
need T1; T3's `ComposeOverlay` is pure and can be built independently first.

### T1 — Entities, EF config, migration, CP wiring (AC1, AC2, AC8, AC9)

**Scope:** Two CP-resident entities with the XOR/index discipline; DbSets; the migration; the
DROP-list + CP-model-test amendments.

**Files (new/modify):** `Tamma.Data/Entities/AgentStyleVariant.cs`,
`Tamma.Data/Entities/AgentStyleVariantSelection.cs`; `Tamma.Data/TammaModelConfiguration.cs`
(both configs — XOR CHECK + `UNIQUE NULLS NOT DISTINCT`); `Tamma.Data/ControlPlaneDbContext.cs`
(two DbSets); `Tamma.Data/Migrations/ControlPlane/*_AddAgentStyleVariants.cs` (generated);
`Tamma.Api/Program.cs` (DROP-list amend — both tables);
`tests/.../Epic28/ControlPlaneDbContextModelTests.cs` (strict list — both entities).

**Tests (first):** `AgentStyleVariantModelTests` — XOR CHECK rejects both/neither principal;
unique-nulls-not-distinct rejects a duplicate `(TenantId, UserId, Name)` variant and a duplicate
`(TenantId, UserId, Role)` binding; `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`
includes both entities; `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext`
→ none; a second test-host boot succeeds (DROP-list proven for both tables).

**Acceptance:**
- [ ] Both entities have all AC1/AC2 fields; `AgentStyleVariant` has **NO** provider/model/credential/base-prompt fields.
- [ ] EF config mirrors `AgentRoleSelection`/`TenantAgentEnablement` (XOR CHECK + unique-nulls-not-distinct) for both tables.
- [ ] Both DROP-list entries + both strict-list entries added; second boot green; `has-pending-model-changes` → none.

### T2 — `IAgentStyleVariantService` CRUD / enable / bind + events (AC5, AC6, AC12)

**Scope:** Create/update/delete own-private variants; `SetEnabledAsync` (own-private implicit-enabled,
disable-own ⇒ 409/no-op like 32-16); `BindAsync(role, variantId?)` (one variant per `(principal,
role)`; null clears; un-enabled/unseen → 404/409). One DCB event per write.

**Files (new):** `Tamma.Api/Services/Agents/IAgentStyleVariantService.cs`,
`Tamma.Api/Services/Agents/AgentStyleVariantService.cs`,
`Tamma.Api/Services/Agents/AgentStyleVariantEventTypes.cs`
(`AGENT.STYLE_VARIANT.CREATED/UPDATED/DELETED/ENABLED/DISABLED/BOUND/UNBOUND.SUCCESS`).

**Collaborators (constructor-injected; fakes in tests):** `ControlPlaneDbContext`,
`ITammaModeProvider`, `ITenantContext`/`ClaimsPrincipal` accessor, `IEventRepository`,
`ILogger<AgentStyleVariantService>`.

**Tests (first):** `AgentStyleVariantServiceTests` — create/update/delete own-private; enable/disable
(implicit-private, disable-own ⇒ 409); bind/clear; each write emits exactly one `AGENT.STYLE_VARIANT.*`
event tagged `{ variantId, variantName, role?, mode, tenantId|userId }`; 404 on unseen target;
cross-tenant target → 404.

**Acceptance:**
- [ ] CRUD/enable/bind upsert correctly; public shipped variants are read-only (write → 403/404 per RBAC).
- [ ] Exactly one event per successful write; tags correct per mode.
- [ ] Binding enforces one variant per `(principal, role)`; `variantId:null` clears.

### T3 — `ResolveActiveVariantAsync` + the pure `ComposeOverlay` (AC3, AC4, AC11)

**Scope:** The two primitives 32-5 calls. `ResolveActiveVariantAsync(role)` returns the single
bound+enabled+visible variant else `null`. `ComposeOverlay(base, variant?)` is **pure** (no I/O):
`null` → base verbatim; variant → `base + delimited style section`, base a verbatim **prefix**.

**Files:** extend `AgentStyleVariantService.cs` (resolve); a pure helper
`StyleOverlayComposer` (or a static method) for `ComposeOverlay` so it is independently testable.

**`ComposeOverlay` contract:**
```
variant == null  => base                                         (no overlay; the default)
variant != null  => base + "\n\n## Response style\n" + RenderDirectives(style)
  RenderDirectives: stable knob lines (tone/verbosity/format/audience) + trimmed stylePrompt.
  Empty/whitespace stylePrompt contributes nothing. Invariant: result.StartsWith(base).
  NEVER edits/truncates/reorders the base.
```

**Tests (first):** `StyleOverlayComposeTests` (pure, no DB) — null → base unchanged;
variant → base is a prefix; empty stylePrompt → no contribution; knob rendering stable/deterministic;
property test `result.StartsWith(base)` over random base + variant. `AgentStyleVariantServiceTests`
(resolve truth table) — bound+enabled→variant; bound+disabled→null; unbound→null; retired→null;
cross-tenant target → null/404.

**Acceptance:**
- [ ] `ResolveActiveVariantAsync` truth table passes; disabled/retired/unbound all → `null` (optional, never an error).
- [ ] `ComposeOverlay` is provably additive (base is a prefix; null → identity); never blanks the base.

### T4 — Endpoints + RBAC + DI (AC5, AC6, AC7)

**Scope:** `/api/style-variants` group: CRUD, `PUT /{id}/enablement`, `PUT /bindings/{role}`, `GET`
list/bindings. Member 403 on writes; reads allowed; owner/admin write own-tenant; shipped catalog
read-only (`PlatformOwnerAccess` for platform mutation, not exposed on the tenant route).

**Files (new/modify):** `Tamma.Api/Endpoints/StyleVariantEndpoints.cs`;
`Tamma.Api/Dtos/Agents/StyleVariantResponse.cs`, `StyleVariantRequest.cs`,
`SetVariantBindingRequest.cs`; `Tamma.Api/Program.cs` (DI registration; route mapping; reuse the
`AgentManage`/member/`PlatformOwnerAccess` policies + `ConfigWrite` rate limiter).

**Tests (first):** `StyleVariantEndpointsTests` (in-process `WebApplicationFactory`) — RBAC matrix:
SaaS member → 403 on create/update/delete/enable/bind; member reads → 200; owner/admin writes → 200;
platform-catalog mutation not exposed (asserted absent/404); DI resolves the chain at host startup.

**Acceptance:**
- [ ] RBAC matrix green; member can read, cannot write; owner/admin write own-tenant.
- [ ] DI resolves `IAgentStyleVariantService` + the endpoints at startup (smoke test).

### T5 — Seeder (shipped public variants, no default binding) (AC10)

**Scope:** `AgentStyleVariantSeeder` seeds a small shipped public catalog (`terse`, `verbose`,
`formal`, `casual`) **insert-missing-only** (never reverts a tenant edit). **Bind nothing** — a fresh
principal has zero bindings → default = no overlay (orthogonality preserved).

**Files (new):** `Tamma.Api/Services/Agents/AgentStyleVariantSeeder.cs`; wire into the existing
seeding startup path alongside `AgentEntitySeeder`/`TenantEnablementSeeder`.

**Tests (first):** `AgentStyleVariantSeederTests` — a fresh principal has the shipped variants
available but **zero bindings**; rerun is insert-missing-only (does not revert a tenant edit or add a
binding).

**Acceptance:**
- [ ] Shipped public variants seeded; no default binding; rerun idempotent (insert-missing-only).

### T6 — Orthogonality + provider-unchanged + isolation + mode tests (AC11, AC13)

**Scope:** The load-bearing guards: default = none byte-for-byte; provider/model/credential identical
with vs without a variant; never weakens base resolution; cross-tenant isolation; mode keying.

**Files:** `tests/Tamma.Api.Tests/Agents/StyleVariantIsolationTests.cs`; extend
`AgentStyleVariantServiceTests` (mode matrix) + an orthogonality golden test + a 32-5-shaped harness
test (or an integration test against the 32-5 step-4b seam once 32-5 lands).

**Tests (first):**
- **Orthogonality golden test:** for a fixed agent + rendered base, **no binding** ⇒ the systemPrompt
  fed to the loop == `ComposeOverlay(base, null)` == base, byte-for-byte.
- **Provider/model/credential unchanged:** same agent, with vs without a bound variant ⇒
  `providerUsed`/`modelUsed`/`credentialSource` identical; only the system prompt differs (additively).
- **Never weakens base:** base prompt that fails to resolve (tenant→system→error) still fails loud
  with a bound variant present; a disabled/missing **variant** is not an error.
- **Cross-tenant isolation:** tenant A's private variant/binding never visible to B; A cannot
  bind/enable B's private variant (404); A's changes never affect B.
- **Mode-parameterized** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): variant + binding keyed by
  `UserId` vs `TenantId`; the other column NULL (XOR); events tag the correct principal.

**Acceptance:**
- [ ] Default = none is byte-for-byte the no-variant output (golden test green).
- [ ] Provider/model/credential identical with/without a variant.
- [ ] Variant never rescues an empty base; isolation + mode matrix green.

---

## Story order & dependencies

External prereqs (conceptual / interface-level): **32-12 rewrite** (locked vocabulary — variant ≠
persona), **32-15** (persona catalog a variant overlays), **32-16** (catalog-membership/XOR pattern),
**32-17** (custom-agent base prompts the overlay rides on after), **32-2** (`AgentRoleSelection`
binding precedent + RBAC policies), **Epic 27** (the base prompt). **32-5** is the **consumer** that
wires step 4b via this story's interface — sequence the 4b insertion in/after 32-5; this story can
land its entity/service/API independently (gated only by the call-site wiring). Internal:
T1 → (T2 ∥ T3) → T4 → T5 → T6.

This is sequence **G** (post-F): optional + orthogonal, after the lynchpin (32-5) renders the base
prompt the overlay rides on.

## EF / migration note

Stories are implemented **sequentially** (single migration snapshot). This story **amends/extends**
the existing `ControlPlane` migration snapshot with one new migration adding `agent_style_variants`
and `agent_style_variant_selections` — it does NOT branch the snapshot. Both tables are CP-resident
(`ControlPlaneDbContext`); they are NOT added to the per-tenant `EfTenantDbMigrator` (which owns
`t_<hex>` tables only). Append both to the `Program.cs` startup-reset DROP list and the
`ControlPlaneDbContextModelTests` strict `BeEquivalentTo` list in the same change.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~StyleVariant|FullyQualifiedName~StyleOverlay"
# CP model contract + pending-changes
sg docker -c "dotnet test apps/tamma-elsa/tests/ --filter FullyQualifiedName~ControlPlaneDbContextModelTests"
dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext --project apps/tamma-elsa/src/Tamma.Data
# orthogonality proof: variant carries no provider/model fields
grep -rn "Provider\|Model\|ApiKey\|credential" apps/tamma-elsa/src/Tamma.Data/Entities/AgentStyleVariant.cs   # expect: none
```

## Risks

- **Vocabulary regression (a variant named/treated as a persona — the §3.4 mistake):** mitigated by a
  strict entity shape (no provider/model fields — grep-proven), a distinct route (`/api/style-variants`),
  a distinct event family (`AGENT.STYLE_VARIANT.*`), and the story title's "NOT a persona."
- **Overlay replaces/blanks the base (empty-fallback regression):** `ComposeOverlay` is a pure suffix;
  base-is-prefix property test; null → identity; the base keeps its tenant→system→error contract in
  32-5 step 4 (the variant never touches it). If a test needs the base to be editable, the design is
  wrong — stop.
- **Default behaviour drift (something changes when nobody bound a variant):** the seeder binds
  nothing; the orthogonality golden test asserts byte-for-byte equality with the no-variant output.
- **Variant changes provider/model/credential/cost:** the entity carries none of those fields; the
  provider-unchanged integration test asserts `providerUsed`/`modelUsed`/`credentialSource` identical.
- **New CP tables break the second test-host boot (`relation already exists`):** amend the `Program.cs`
  DROP list for **both** tables; second-boot test proves it.
- **Strict CP model-test fails after adding the entities:** update the `BeEquivalentTo` list for both
  in the same change (known gotcha, not a regression).
- **Overlap with 32-5's composition:** hard boundary — ship the interface + primitives; 32-5 owns step
  4b. No `ManagedAgent.RunAsync` edits here.
- **XOR/keying drift from `AgentRoleSelection`/`TenantAgentEnablement`:** mirror the
  `TammaModelConfiguration` config (XOR check name pattern, unique-nulls-not-distinct); constraint tests.
- **Style fragment as an injection vector:** the fragment is appended to the **system** prompt of a run
  the principal already controls; it is owner/admin-authored config, not user content; sanitization
  stays in 32-5's loop; the overlay adds no tool/credential surface; log at length-summary granularity.
