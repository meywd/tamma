# Story 32-12: Persona-Aware Benchmarking

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), Knowledge Base usage (`.dev/` directory), TRACE/DEBUG logging requirements, Test-Driven Development, 100% critical-path coverage, and build-success enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user) choosing which named system persona to trust with a given role**,
I want **per-tenant benchmark leaderboards keyed by the named cross-role PERSONA-agent (`claude`/`gemini`/`codegpt`) — paired with its provider, model, prompt-version, and `credentialSource` — so I can compare, on my own work, "which persona is the best reviewer for me?" like-vs-like within a role**,
So that **I can pick the best-performing persona for each role on real downstream quality and cost (success rate, iterations-to-done, defect rate, latency, cost basis), certain my per-persona performance data never leaks to another tenant or to the platform owner who curates the public persona catalogue**.

> **Vocabulary reframe (v2.0.0, 2026-06-21).** In the locked agent model (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3.0), **PERSONA = the named, cross-role public/system agent** (`claude`/`gemini`/`codegpt`) that presets `{provider, model, config}` — the entity reframed by **Story 32-15** (the former per-role `tamma-<role>` concept). It is **NOT** a style/tone overlay. The old style/voice idea (`atlas`/`nova` tone/verbosity within a role) is split out to optional sibling **Story 32-19 "Agent style/voice variants"** (a *variant*/style profile, never a *persona*). This story therefore **adds no new entity** and introduces **no style-overlay framing** — it is purely the persona-aware *benchmarking* dimension over the named persona-agent identity.

## Priority

P1 — the persona-aware read-model payoff of the Epic 32 tracking stack, riding directly on the 32-10 benchmark projection. 32-6 captures the action trail, 32-8 scores outcomes + classifies defects, 32-9 emits usage/cost, 32-10 folds them into per-agent/provider/prompt/**persona** leaderboards; this story makes the **persona dimension** correct under the locked model (keyed by the 32-15 persona-agent `AgentId`/`AgentName`, not a style row) and adds the persona-comparison facets a tenant reads to pick a persona per role. Valuable, not foundational: 32-10 already ships a `persona` dimension; this story populates it with the right identity + facets and the comparison API.

## Context

### What 32-10 already ships (the substrate)

Story 32-10 (`docs/stories/epic-32/story-32-10/32-10-benchmark-projections-and-leaderboards.md`) ships `BenchmarkProjectionService` — an idempotent, `SequenceNumber`-cursor-tracked fold over the resolving tenant's `domain_events` that materialises **per-agent / per-provider / per-prompt / per-persona** `BenchmarkProjection` rows in the tenant's own `t_<hex>` schema, plus a member-readable leaderboard API (`GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=…`). Its `persona` dimension already exists with `DimensionKey = personaId`, but 32-10 explicitly defers the *semantics* to this story: "Story 32-12 enriches persona attribution. This story ships the dimension; 32-12 populates richer persona semantics."

### What "persona" means now (the reframe this story implements)

Under the locked model, the `persona` dimension's `DimensionKey` is the **named persona-agent's identity** — the public `Agent` (`claude`/`gemini`/`codegpt`) that 32-15 seeds with `Role = NULL`, an explicit `provider`+`model`, and prompts from the Epic 27 store. So:

| | Old 32-12 (superseded) | This rewrite (locked model) |
|---|---|---|
| **Persona** | a style/tone overlay (`atlas`/`nova`) *within* one role | the **named cross-role system agent** (`claude`/`gemini`/`codegpt`) from 32-15 |
| **New entity** | a `Persona` table (style + prompt-fragment) | **none** — persona IS the public `Agent`; benchmark rides 32-10's projection |
| **Benchmark key** | `(role, personaId)` where persona = style | `(role, personaAgentId)` where persona = the public agent + its provider/model/prompt-version/credentialSource |
| **Style/voice** | conflated into "persona" | split out → **32-19** style/voice variants |

The **action trail (32-6)** already tags each `AGENT.TASK.*` / `AGENT.ITERATION.COMPLETED` / `AGENT.PANEL.AGGREGATED` event with `agentId`/`agentVersion`/`provider`/`promptRef` (32-6/32-15). For a public persona, `agentId` **is** the persona's identity. This story makes the **persona-aware view** of that data first-class: it pins the persona dimension to the persona-agent identity (not a style row), adds `credentialSource` and prompt-version as benchmark facets, and exposes the persona-comparison query so "`claude` vs `gemini` as my reviewer" is answerable like-vs-like.

### Explicitly out of scope (referenced, not implemented)

- **The persona entity + seeding** (`Agent.Role` nullable, named cross-role personas, Epic-27 prompt wiring) — **Story 32-15**. This story consumes the persona-agent identity; it does not define personas.
- **Per-tenant persona enablement** (`TenantAgentEnablement`) — **Story 32-16**. Only enabled personas appear in a tenant's leaderboard (a persona the tenant never enabled has no runs).
- **The base benchmark projection + leaderboard API + tenant isolation + k-anonymity admin rollup** — **Story 32-10**. This story extends its `persona` dimension; it does not rebuild the fold.
- **Agent style/voice variants** (the `atlas`/`nova` tone/verbosity overlay, formerly mis-modelled as "persona") — **Story 32-19** (NEW optional sibling). A *variant* is a style profile composed onto a persona-agent; it is a different feature with its own visibility/XOR/index discipline and (eventually) its own benchmark facet. **Not built here.**

## Acceptance Criteria

1. **Persona = the named persona-agent identity (no new entity).** The benchmark `persona` dimension's `DimensionKey` is the **public persona-agent's identity** — `AgentId` (immutable) with `AgentName` (the stable handle `claude`/`gemini`/`codegpt` from 32-15) as the human-readable label. This story adds **no** `Persona` table, no `PersonaComposer`, and no style-overlay row. It rides 32-10's existing `BenchmarkProjection`/`BenchmarkProjectionCursor` tenant entities. (If 32-10's `persona` dimension still keyed a style row, this story corrects it to the persona-agent identity; if it is already keyed by `agentId`, this story leaves the shape and adds the facets in AC2/AC5.)

2. **Persona benchmark facets include provider, model, prompt-version, and credentialSource.** A persona's benchmark row (or its readable detail) carries, alongside the 32-10 metrics, the **dimensions that disambiguate a persona's runs**: `provider` and `model` (from the persona-agent's resolved config — 32-15), `agentVersion` (the pinned persona version), `promptRef`/prompt-version (the Epic-27 prompt that produced the run — 32-6), and **`credentialSource ∈ {byok, platform}`** (from 32-3, surfaced on the run by 32-5/32-9). These ride as **flat-string sub-keys / facets on the persona projection** so "`claude` on BYOK Anthropic vs `claude` on platform Anthropic" and "`claude` under prompt v3 vs v4" are distinguishable within the persona. No raw config, prompt body, or key is ever a facet value.

3. **Per-tenant scoping is absolute (design ownership rule).** Persona performance/action data is **ALWAYS tenant-scoped** — every persona benchmark row lives in the resolving tenant's `t_<hex>` schema, written/read only through `ITenantDbContextFactory` (32-10's isolation backbone), and carries `TenantId` = the resolving tenant. Two tenants both running public persona `claude` build **separate, private** persona profiles; the platform owner who curates `claude` sees neither. There is **no cross-tenant and no platform-admin read path** for a tenant's per-persona rows (the only admin view is 32-10's k-anonymous, public-agent-only fleet rollup — owned there, not extended here). **Explicitly tested.**

4. **Persona-comparison is like-vs-like within a role.** The leaderboard's `persona` dimension ranks personas **within a single role** for the calling tenant: "which persona has the best success rate / fewest functional defects / lowest cost basis as my **reviewer**?" compares `claude` vs `gemini` vs `codegpt` **scoped to `role=reviewer`**, never a reviewer-run against an architect-run. The query pairs the persona key with its `role` so cross-role comparison is structurally impossible (a persona used for two roles yields two separate per-role rows). Ranking reuses 32-10's deterministic tie-break chain (`successRate` desc → `avgIterationsToDone` asc → `avgCostBasisUsd` asc → key asc) and its `minRuns` min-sample guard (provisional rows in `belowThreshold`, never silently ranked).

5. **Persona-aware leaderboard query facets.** The 32-10 leaderboard endpoint `GET /api/v1/orgs/{tenantId}/agents/leaderboard` accepts the persona dimension plus role + facet filters: `?dimension=persona&role=reviewer&window=30d` ranks personas as that role; optional `?personaId=` returns one persona's detail (its facet breakdown across provider/model/promptVersion/credentialSource); optional `&credentialSource=byok|platform` and `&promptVersion=` narrow the comparison. The response is **member-readable** (any tenant member); there is no tenant mutation surface for persona benchmark rows (they are derived). The endpoint shape is identical between modes — the auth/tenant filter (`RequireTenantMembershipFilter`) scopes the read to the caller's tenant.

6. **The fold consumes only existing trail tags — no new source event family.** This story adds **no** new `AGENT.*` source event. It folds the same 32-6/32-8/32-9 events 32-10 already folds; the persona dimension's `dimensionKeyFor` reads `agentId` (the persona-agent identity) from the event `Tags`, and the facet sub-keys (`provider`, `model`, `agentVersion`, `promptRef`, `credentialSource`) from the same flat tags 32-6/32-9 already emit. Where a trail tag is missing (e.g. `credentialSource` on a pre-32-5 run), the facet degrades to an explicit `unknown`/`null` bucket — **never silently merged** into a populated facet (no-empty-fallback discipline; fail-loud on a malformed key, degrade-explicitly on an absent one).

7. **Idempotent, cursor-tracked, replayable (32-10 contract preserved).** Persona projection folds are idempotent with the **per-`(dimension, dimensionKey, window)` `SequenceNumber` cursor** 32-10 defines; re-folding the same events is a no-op; a full `RebuildAsync` (cursor reset → replay from 0) produces byte-identical persona rows to the incremental path. **Per-tenant cursor rule:** the cursor is the tenant-schema `domain_events.SequenceNumber` (an independent per-schema `BIGSERIAL`); the persona projection cursor is composite-keyed including `TenantId` — there is **no shared global cursor across tenants** (compliance/billing/audit-grade isolation). Adding the persona facets does **not** change the cursor mechanics.

8. **DCB lifecycle events for the persona projection (no source events).** Persona-projection lifecycle reuses 32-10's `BENCHMARK.PROJECTION.UPDATED` / `BENCHMARK.PROJECTION.REBUILT` event family (tagged `{ tenantId, dimension: "persona", window }`); this story emits **no new** persona-specific source event and **no** "lie" event for an empty fold. Events land in the resolving tenant's store (`TenantId` set); persona-projection events are never cross-tenant and never on the control plane.

9. **No new control-plane table; no DROP-list / model-contract churn.** This story adds **no** control-plane table (the persona IS the 32-15 public `Agent`, already in the CP DROP list + `ControlPlaneDbContextModelTests`). The persona benchmark rows are the 32-10 **tenant-schema** `BenchmarkProjection`/`BenchmarkProjectionCursor` entities (owned by the per-tenant `EfTenantDbMigrator`, NOT the CP DROP list). So: **nothing** is appended to `Program.cs`'s startup-reset "Wiping Tamma-managed public-schema tables" DROP list, and `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` is **not** touched. If facets require a column on the tenant `BenchmarkProjection` entity, it is an additive **Tenant** migration (single linear snapshot), `has-pending-model-changes` reports none.

10. **No regression on the base leaderboard.** The agent/provider/prompt dimensions and their leaderboard shape are byte-for-byte unchanged; a non-persona leaderboard query returns exactly what 32-10 returns. The persona dimension's metric values (successRate, iterations, defect-by-category, p50/p95, cost basis / billable) are computed by the **same 32-10 reducer** — this story only refines the dimension key + adds facets, never re-derives the metrics or re-implements the fold. The full `Tamma.Api.Tests` suite stays green.

11. **Tests** cover: persona dimension key = the persona-agent `AgentId`/`AgentName` (NOT a style row); facet breakdown by provider/model/promptVersion/credentialSource (BYOK vs platform `claude` distinguished; prompt v3 vs v4 distinguished); like-vs-like within-a-role comparison (a persona used as reviewer and architect yields two separate per-role rows; an architect-role persona never appears in the reviewer leaderboard); per-tenant isolation (tenant B's persona rows never appear in tenant A's leaderboard; platform admin denied per-tenant); incremental-vs-rebuild equivalence on the persona dimension; absent-facet explicit-`unknown` bucketing (no silent merge); min-sample guard on persona rows; and `BENCHMARK.PROJECTION.*` emission with `dimension:"persona"` (no new source event, no-emission-on-empty-fold).

## Technical Design

### Architectural placement (per the locked model)

Per `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3.0/§3.4: **persona = the named cross-role public/system `Agent`** (32-15). Benchmarking is a **read model derived from the tenant's DCB event stream**, owned by **32-10**; this story is the **persona-aware refinement** of that read model. It introduces no entity, no provider config, no style overlay — it keys the `persona` dimension on the persona-agent identity and adds the facets that make a persona's runs comparable like-vs-like within a role, on the tenant's own data.

Ownership & data scoping (the design's load-bearing rule, applied to persona benchmarks):

| Concern | Scope |
|---|---|
| **Persona definition** | Public/system — the named cross-role `Agent` curated by the platform owner (`PlatformOwnerAccess`), seeded by 32-15, usable by every tenant that **enables** it (32-16). Not owned/edited by tenants. |
| **Persona performance/benchmark data** | **ALWAYS tenant-scoped** — the tenant that generated it owns it; lives in its `t_<hex>` schema; never cross-tenant; the platform owner who curates a public persona sees **none** of any tenant's per-persona metrics. |

Story 32-10 (the `BenchmarkProjectionService`, the `persona` dimension, the leaderboard API, the `SequenceNumber` cursor, tenant isolation, and the k-anonymous admin rollup) and Story 32-15 (the persona-agent identity — `AgentId`/`AgentName`, `provider`/`model`) are **hard prerequisites**; both are referenced **by interface** and **by event-tag contract**.

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      BenchmarkProjection.cs            # REUSE (32-10) — persona dimension row; OPTIONAL additive facet cols
                                        #   (Provider, Model, AgentVersion, PromptRef, CredentialSource)
      BenchmarkProjectionCursor.cs      # REUSE (32-10) — per-(dimension,key,window) SequenceNumber cursor
    TammaModelConfiguration.cs          # MODIFY (only if AC2 facets need additive tenant columns)
    Migrations/Tenant/
      <ts>_AddPersonaBenchmarkFacets.cs # NEW (only if AC2 facets are persisted columns; additive, Tenant ctx)
  Tamma.Api/
    Services/Agents/
      BenchmarkProjectionService.cs     # MODIFY — persona dimensionKeyFor = agentId; facet sub-keys;
                                        #   GetLeaderboardAsync persona role+facet filters
      IBenchmarkProjectionService.cs    # MODIFY (only if the persona-comparison read needs a new method)
      PersonaBenchmarkFacets.cs         # NEW — pure facet extraction from flat trail Tags (provider/model/
                                        #   agentVersion/promptRef/credentialSource); unknown-bucket policy
    Endpoints/
      AgentLeaderboardEndpoints.cs      # MODIFY (32-10) — persona dimension: ?role=, ?personaId=,
                                        #   &credentialSource=, &promptVersion= facets
    Dtos/Agents/
      BenchmarkDtos.cs                  # MODIFY (32-10) — PersonaLeaderboardRow + facet breakdown DTO
    Program.cs                          # MODIFY — (no new route group; facet query params on the 32-10 route)
```

> **No new entity, no new service.** The persona-aware view is a **refinement of 32-10's `persona` dimension** plus a pure facet-extraction helper. If the facets (provider/model/promptVersion/credentialSource) are computed at query time from the folded sub-keys, even the additive tenant columns are unnecessary — prefer the no-schema-change path and persist facets only if query-time aggregation is too costly.

### Persona dimension key + facets (the only genuinely new logic)

```csharp
// In BenchmarkProjectionService.dimensionKeyFor — persona = the persona-AGENT identity.
// (Locked model: persona = the named public Agent from 32-15, NOT a style row.)
string DimensionKeyFor(string dimension, IReadOnlyDictionary<string, string> tags) => dimension switch
{
    "agent"    => tags["agentId"],
    "provider" => tags["provider"],
    "prompt"   => tags["promptRef"],
    "persona"  => tags["agentId"],   // a public persona's agentId IS its persona identity (32-15)
    _ => throw new TammaError("BENCHMARK.DIMENSION.INVALID", $"Unknown dimension '{dimension}'.",
                              retryable: false, severity: Severity.High)
};
```

```csharp
// PersonaBenchmarkFacets — pure extraction of the disambiguating facets from the SAME flat trail tags.
// Absent facet => explicit "unknown" bucket (never silently merged into a populated facet).
public sealed record PersonaFacets(
    string Provider, string Model, int AgentVersion, string PromptRef, string CredentialSource);

public static PersonaFacets Extract(IReadOnlyDictionary<string, string> tags) => new(
    Provider:         tags.GetValueOrDefault("provider",         "unknown"),
    Model:            tags.GetValueOrDefault("model",            "unknown"),
    AgentVersion:     int.TryParse(tags.GetValueOrDefault("agentVersion"), out var v) ? v : 0,
    PromptRef:        tags.GetValueOrDefault("promptRef",        "unknown"),
    CredentialSource: tags.GetValueOrDefault("credentialSource", "unknown"));   // byok | platform | unknown
```

The persona row's **identity** is `(role, agentId)` (like-vs-like within a role, AC4); the **facets** sub-slice that identity so a tenant can ask "is `claude` better for me on BYOK Anthropic than on the platform key?" or "did prompt v4 regress `claude`'s defect rate?" without ever comparing across roles or tenants.

### Persona-comparison leaderboard (extends the 32-10 endpoint)

```
GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=persona&role=reviewer&window=30d
      -> 200 { dimension:"persona", role:"reviewer", window:"30d",
               ranked:[ { personaId, personaName, provider, model, successRate, avgIterationsToDone,
                          defectRateByCategory, latencyP50Ms, latencyP95Ms, avgCostBasisUsd,
                          avgBillableUsd, runCount } ... ],   // ordered by 32-10 tie-break chain
               belowThreshold:[ ... ] }                       // runCount < minRuns (32-10 guard)

GET …?dimension=persona&personaId={agentId}&role=reviewer&window=30d
      -> 200 { personaId, personaName, role:"reviewer",
               facets:[ { provider, model, promptVersion, credentialSource, ...metrics, runCount } ... ] }

GET …?dimension=persona&role=reviewer&credentialSource=byok            // facet filter
GET …?dimension=persona&role=reviewer&promptVersion=v4                 // facet filter
```

All reads are member-scoped by `RequireTenantMembershipFilter` to the caller's tenant (32-10). A tenant requesting another tenant's path → 403/404 (32-10's isolation, unchanged). The platform owner has **no** per-tenant persona route (the only cross-tenant view is 32-10's k-anonymous public-agent fleet rollup).

### Source events (consumed, not produced)

This story produces **no** source event. It folds the persona dimension from the same families 32-10 folds:

| Source event | Producer | Persona-fold contribution |
|---|---|---|
| `AGENT.TASK.SUCCESS`/`.FAILED`/`.PARTIAL` | 32-6 | persona key = `agentId`; run latency; success numerator/denominator; provider/model/credentialSource facets |
| `AGENT.OUTCOME.RECORDED` | 32-8 | `iterationsToDone` for the persona's `avgIterationsToDone` |
| `AGENT.DEFECT.RECORDED` | 32-8 | per-category defect rate for the persona (`BugCategory` wire strings) |
| `AGENT.ITERATION.COMPLETED` / `AGENT.PANEL.AGGREGATED` | 32-6/32-7 | iteration fallback; panel persona attribution |
| `AGENT.USAGE.RECORDED` | 32-9 / 32-5 | `costBasisUsd`/`billableUsd`; **`credentialSource`** facet; provider/model facets |

> The `credentialSource` facet (AC2) is the only facet that depends on 32-5/32-9 (the call-LLM endpoint surfaces `credentialSource` on the run, 32-9 tags it on `AGENT.USAGE.RECORDED`). Until those land, the facet degrades to the explicit `unknown` bucket (AC6), backfilled on the next rebuild once the tag exists — **never** silently merged.

### DCB lifecycle (reuses 32-10's family)

| Event | When | Tags |
|---|---|---|
| `BENCHMARK.PROJECTION.UPDATED` | persona fold advances the cursor | `{ tenantId, dimension: "persona", window, rowsUpdated }` |
| `BENCHMARK.PROJECTION.REBUILT` | persona dimension rebuilt (cursor reset → replay) | `{ tenantId, dimension: "persona", window }` |

No persona-specific source event; no event on an empty fold. Appended via the tenant `IEventRepository` into the resolving tenant's store (`TenantId` set) — never the control plane, never cross-tenant.

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a **persona** (the definition)? | The platform — personas are public `Agent`s (`PlatformOwnerAccess` to curate); the sole user uses an enabled subset. | The platform — same public personas, shared cross-tenant; tenants enable a subset (32-16), never edit. |
| Who owns the **per-persona benchmark data**? | The sole user — their instance, their dataset (`UserId`-keyed tenant). | The tenant that generated it — **always** tenant-scoped; one persona definition → many independent per-tenant datasets. |
| Who reads the persona leaderboard? | The user (member-read). | Any tenant member (`MemberAccess` + `RequireTenantMembershipFilter`) for their own tenant only. |
| Can a platform admin read a tenant's persona benchmark? | N/A (sole user). | **No.** Per-persona rows are structurally unreachable cross-tenant; the only admin view is 32-10's k-anonymous **public-agent** fleet rollup — never a single tenant's persona row, never a tenant id. |
| Where do persona projection rows + cursors live? | The single tenant store (`t_<hex>`). | The originating tenant's `t_<hex>` schema (per-tenant routing via `ITenantDbContextFactory`); per-tenant `SequenceNumber` cursor. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Integration points

- **Story 32-10** (`BenchmarkProjectionService`, `BenchmarkProjection`/`BenchmarkProjectionCursor`, the leaderboard API, the `SequenceNumber` cursor, tenant isolation, the k-anonymous admin rollup) — this story refines its `persona` dimension key and adds facets + the persona-comparison query. **Hard prerequisite; by interface.**
- **Story 32-15** (persona reframe + seeding) — supplies the persona-agent identity (`AgentId`/`AgentName`, nullable `Role`, explicit `provider`/`model`); for a public persona, `agentId` (trail tag) **is** the persona identity. **Hard prerequisite; by event-tag contract.**
- **Story 32-16** (per-tenant enablement) — only enabled personas have runs in a tenant; the persona leaderboard naturally reflects the enabled set.
- **Story 32-6** (action trail) — the `agentId`/`agentVersion`/`provider`/`promptRef` flat tags this story keys + facets on. **By tag contract.**
- **Story 32-9 / 32-5** (usage/cost + call-LLM) — surface `credentialSource` + `costBasisUsd`/`billableUsd` on `AGENT.USAGE.RECORDED`; the `credentialSource` facet (AC2) consumes them, degrading to `unknown` until they land.
- **`ITenantDbContextFactory` / `IEventRepository`** — the structural isolation plane + the cursor-paged, tenant-scoped, null-tenant-guarded read (32-10).
- **`RequireTenantMembershipFilter` + `/api/v1/orgs/{tenantId}`** — the member-readable tenant-path the leaderboard rides (32-10).
- **`ITammaModeProvider`** (`Tamma.Api/Services/PromptStore/TammaMode.cs`) — per-mode principal derivation.

## Dependencies

**Internal (hard prerequisites):**

- **Story 32-10** (Benchmark projections & leaderboards) — owns the fold, the `persona` dimension, the leaderboard API, the `SequenceNumber` cursor, tenant isolation, and the k-anonymous admin rollup. This story extends its persona dimension; it does **not** rebuild the projection engine.
- **Story 32-15** (Persona reframe + seeding) — defines the persona-agent identity (`AgentId`/`AgentName`, `provider`/`model`) this story keys on. Without it there is no "persona" to benchmark.

**Internal (consumed by tag contract / soft-degrade):**

- **Story 32-6** (Agent action trail) — the `agentId`/`agentVersion`/`provider`/`promptRef` tags the persona fold reads.
- **Story 32-8** (Outcome capture & bug taxonomy) — outcome/defect events the persona metrics fold.
- **Story 32-9** (Agent usage & cost emission) + **Story 32-5** (Call-LLM endpoint) — surface `credentialSource` + cost on the run; the `credentialSource` facet degrades to `unknown` until they land (no parallel event invented).
- **Story 32-16** (Per-tenant enablement) — constrains which personas a tenant runs (and thus benchmarks).

**Consumers / related (downstream, not blockers):**

- **Story 32-13** (Agent management & benchmark dashboards) — surfaces the persona-comparison leaderboard + facet breakdown in the UI.
- **Story 32-14** (A/B experiment framework) — may compare two personas as an experiment arm using this dimension.

**Related sibling (split-out, NOT a dependency):**

- **Story 32-19** (Agent style/voice variants — NEW) — the `atlas`/`nova` style/voice overlay formerly mis-modelled as "persona." A *variant* is a style profile composed onto a persona-agent (its own visibility/XOR/index discipline). If 32-19 ships its own variant benchmark facet, it aligns with this dimension; **not built or assumed here**.

**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0 reframe table, §3.1 persona = system-agent entity, §3.4 disposition of 32-12).

**Related project rule:** `feedback_resolution_no_empty_fallback` — facet/dimension resolution fails loud on a malformed key and degrades to an **explicit** `unknown` bucket on an absent one; it never silently merges into a populated facet.

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`. Docker-bound suites run via `sg docker -c "dotnet test ..."` (see `reference_dotnet_test_docker`). TDD: write the failing test first.

1. **Persona dimension key = persona-agent identity** (`BenchmarkProjectionServiceTests`): a fold over a fixture stream where `agentId` is the public `claude` persona produces a persona row keyed by that `agentId`/`AgentName` — **not** a style row; assert the key equals the 32-15 persona-agent identity, never a tone/verbosity value.
2. **Facet breakdown** (`PersonaBenchmarkFacetsTests` / `BenchmarkProjectionServiceTests`): runs of persona `claude` split by `credentialSource` (`byok` vs `platform`) yield distinct facet buckets with the same `personaId`; prompt v3 vs v4 (`promptRef`) split distinctly; same for provider/model; no raw config/prompt/key appears in any facet value.
3. **Like-vs-like within a role** (`BenchmarkLeaderboardEndpointsTests`): persona `claude` used as `reviewer` and as `architect` yields two separate per-role rows; `?dimension=persona&role=reviewer` ranks `claude`/`gemini`/`codegpt` only as reviewer; an architect-role persona row never appears in the reviewer leaderboard.
4. **Per-tenant isolation** (`BenchmarkIsolationTests`, docker-bound): seed persona rows for tenant A and B; `GET …/orgs/{B}/…?dimension=persona` returns only B's; a member of A hitting B's path → 403/404; a platform owner has no per-tenant persona route; a public persona run by A leaves persona rows only in A's schema.
5. **Incremental-vs-rebuild equivalence on the persona dimension** (`BenchmarkProjectionServiceTests`): fold persona events, fold again (cursor skip → no double-count), then `RebuildAsync` (reset → replay) → byte-identical persona rows + facets; the per-tenant composite cursor never crosses tenants.
6. **Absent-facet explicit bucketing** (`PersonaBenchmarkFacetsTests`): a pre-32-5 run with no `credentialSource` tag lands in an explicit `unknown` facet, **never** merged into `byok`/`platform`; a malformed `dimension` throws `BENCHMARK.DIMENSION.INVALID` (no silent fallback).
7. **Min-sample guard on persona rows** (`BenchmarkLeaderboardEndpointsTests`): a persona with `runCount < minRuns` lands in `belowThreshold`, never `ranked` (32-10 guard reused).
8. **DCB lifecycle** (`BenchmarkProjectionServiceTests`): a persona fold emits `BENCHMARK.PROJECTION.UPDATED` with `dimension:"persona"`; a rebuild emits `BENCHMARK.PROJECTION.REBUILT`; an empty fold emits nothing; **no** new persona-specific source event exists (grep proves it).
9. **No regression** (`BenchmarkProjectionServiceTests` / `BenchmarkLeaderboardEndpointsTests`): agent/provider/prompt dimensions + their leaderboard shape unchanged; persona metric values come from the same 32-10 reducer (assert identical numbers for the same runs); no CP table added; `has-pending-model-changes` reports none; the `Tamma.Api.Tests` suite stays green.

**Coverage**: critical paths (persona dimension key, facet extraction + unknown-bucket, like-vs-like role scoping, tenant isolation, cursor idempotency) → 100%; supporting line ≥ 80%.

## Estimated Effort

2-3 days (a refinement of 32-10's persona dimension + facet extraction + the comparison query — no new entity, no new fold, no new service).

## Files Created / Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/PersonaBenchmarkFacets.cs` | Create (pure facet extraction + unknown-bucket policy) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/PersonaBenchmarkFacetsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/PersonaLeaderboardTests.cs` | Create (persona key, like-vs-like, facets, min-sample) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` | Modify (32-10) — persona `dimensionKeyFor` = `agentId`; facet sub-keys; persona role+facet read |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IBenchmarkProjectionService.cs` | Modify (32-10) — only if the persona-comparison read needs a new method signature |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs` | Modify (32-10) — persona `?role=`, `?personaId=`, `&credentialSource=`, `&promptVersion=` facets |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/BenchmarkDtos.cs` | Modify (32-10) — `PersonaLeaderboardRow` + facet-breakdown DTO |
| `apps/tamma-elsa/src/Tamma.Data/Entities/BenchmarkProjection.cs` | Modify (32-10) — ONLY if AC2 facets are persisted columns (else query-time; no change) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify — ONLY if persisted facet columns are added (additive, tenant entity) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddPersonaBenchmarkFacets.cs` | Create — ONLY if persisted facet columns are added (additive **Tenant** migration) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkIsolationTests.cs` | Modify (32-10) — add persona-dimension isolation case |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs` | Modify (32-10) — add persona key + equivalence + lifecycle cases |

> **Prefer the no-schema-change path:** compute the facets at query time from the folded sub-keys. Persist facet columns (the last three rows) **only** if query-time aggregation proves too costly. Either way, this is a **Tenant** migration (owned by `EfTenantDbMigrator`), never a control-plane table — so the `Program.cs` DROP list and `ControlPlaneDbContextModelTests` are untouched.

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (esp. `feedback_resolution_no_empty_fallback`)
3. Read the design of record §3.0 (the reframe table — persona = named cross-role system agent) and §3.4 (disposition of 32-12 — rewrite + split the style overlay to 32-19) IN FULL
4. Read **32-10** (the benchmark projection + leaderboard + `persona` dimension + cursor + isolation you extend) and **32-15** (the persona-agent identity you key on) — confirm 32-10's `persona` dimension key and `BenchmarkProjection` shape before refining them
5. Confirmed which trail tags 32-6/32-9 actually emit (`agentId`/`agentVersion`/`provider`/`promptRef`/`credentialSource`) so the facet extraction reads real keys
6. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **Persona = the named system agent, NOT a style overlay.** This is the whole reframe (design §3.0/§3.4). The persona dimension keys on the 32-15 persona-agent identity (`AgentId`/`AgentName` = `claude`/`gemini`/`codegpt`). The old "style/tone variant within a role" is a **different** feature, split out to 32-19. No `Persona` table, no `PersonaComposer`, no `StyleJson` here.
- **No new entity; ride 32-10.** Benchmarking already exists; this is a persona-aware *refinement* of 32-10's `persona` dimension + facets + the comparison query. Building a parallel projection would fork the read model — forbidden.
- **Facets disambiguate a persona's runs.** Provider/model/promptVersion/`credentialSource` are flat-string facets so "`claude` on BYOK vs platform" and "`claude` under prompt v3 vs v4" are answerable within one persona, without comparing across roles or tenants.
- **Like-vs-like within a role.** A persona is cross-role (32-15), but a comparison is always scoped to **one role** — `(role, agentId)` — so "best reviewer persona" is meaningful and "reviewer vs architect" is structurally impossible.
- **Data is always tenant-scoped.** Even for a *public* persona, the per-persona benchmark belongs to the resolving tenant; the platform owner who curates the persona sees none of any tenant's per-persona metrics (design ownership rule). The only cross-tenant view is 32-10's k-anonymous public-agent fleet rollup — owned there.
- **Fail loud on malformed, explicit-unknown on absent.** A bad `dimension` throws; an absent facet tag buckets to an explicit `unknown` (never silently merged) — `feedback_resolution_no_empty_fallback`, applied to projection facets.

### Codebase gotchas (baked into the AC)

- **No control-plane table → no DROP list / no model-contract churn.** The persona is the 32-15 public `Agent` (already CP-registered); the benchmark rows are 32-10's **tenant-schema** entities (owned by `EfTenantDbMigrator`). So nothing is appended to `Program.cs`'s "Wiping Tamma-managed public-schema tables" DROP list, and `ControlPlaneDbContextModelTests` is not edited (AC9).
- **Per-tenant cursor rule.** The fold cursor is the tenant-schema `domain_events.SequenceNumber` (an independent per-schema `BIGSERIAL`); the persona projection cursor is composite-keyed including `TenantId` — **no shared global cursor across tenants** (32-10's rule, reaffirmed for the persona dimension). Compliance/billing/audit-grade isolation depends on it.
- **`PlatformOwnerAccess`, never `OwnerAccess`.** Persona-catalogue curation (32-15) is `PlatformOwnerAccess`; persona *benchmark reads* are tenant-member (`MemberAccess` + `RequireTenantMembershipFilter`). There is no platform-global persona-benchmark admin route here (the k-anonymous public-agent rollup is 32-10's).
- **Sequential migration discipline.** If facets are persisted, it is a single additive **Tenant** migration on the existing linear snapshot (not a branch); `has-pending-model-changes` → none.

### Edge cases

- A persona used for two roles (cross-role by design) → two separate per-role rows; a comparison always names a role, so the rows never collide.
- A run with no `credentialSource` tag (pre-32-5) → explicit `unknown` facet bucket; backfilled on the next rebuild once 32-5/32-9 land (never silently merged into `byok`/`platform`).
- A persona a tenant never enabled (32-16) → zero runs → absent from the tenant's leaderboard (no synthetic row).
- Two tenants both running public persona `claude` → separate per-tenant rows; neither sees the other; the platform owner sees neither (only the k-anonymous fleet rollup, 32-10).
- A persona archived in 32-15 mid-window → its historical persona rows are unchanged (the fold is over immutable trail events); no retroactive rewrite.

### Migration discipline (Epic 28 conventions, only if facets are persisted)

- Facet columns (if added) are an **additive Tenant** migration: `dotnet ef migrations add AddPersonaBenchmarkFacets` against the **Tenant** context (the `InitialTenant` baseline exists — additive, not a baseline edit), `has-pending-model-changes` → none.
- Mirror entity config **only** in `TammaModelConfiguration.cs`; the snapshot/Designer are generated, not hand-edited.
- Tenant-schema tables do **not** go in the `Program.cs` DROP list (the per-tenant `EfTenantDbMigrator` owns them) and do **not** touch `ControlPlaneDbContextModelTests`.
- Run C# tests with `sg docker -c "dotnet test ..."` (session docker group is stale; build needs no wrapper).

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Persona dimension still keyed by a style row (old model leaks through) | High | AC1 pins the key to the 32-15 persona-agent `AgentId`; explicit test asserts the key is the persona-agent identity, never a tone/verbosity value; no `Persona`/`StyleJson` type exists. |
| Cross-tenant leakage of per-persona performance data | Critical | Reuse 32-10's structural isolation (`ITenantDbContextFactory`, null-tenant-guarded reads, no cross-tenant route, per-tenant composite cursor); explicit isolation test incl. platform-admin-denied. |
| A facet silently merges absent values (no-empty-fallback regression) | High | Absent facet → explicit `unknown` bucket; malformed dimension → throw; facet-bucketing test asserts no silent merge. |
| Reimplementing the 32-10 fold/metrics (drift) | High | This story keys + facets only; metrics come from the same 32-10 reducer; assert identical numbers for the same runs; no second fold. |
| `credentialSource` facet depends on un-landed 32-5/32-9 | Medium | Facet degrades to explicit `unknown`, backfilled on rebuild once the tag exists; align the tag name with 32-9 before merge; never invent a parallel usage event. |
| Cross-role persona comparison sneaks in | Medium | Persona key is `(role, agentId)`; the comparison query always names a role; test asserts an architect-role persona never appears in the reviewer leaderboard. |
| Accidental CP table / DROP-list churn | Medium | AC9: no CP table (persona = 32-15 public `Agent`; rows are tenant-schema 32-10 entities); facets (if persisted) are a Tenant migration; `ControlPlaneDbContextModelTests` untouched. |

## Success Metrics

- [ ] The persona benchmark dimension is keyed by the 32-15 persona-agent identity (`AgentId`/`AgentName`); grep finds no `Persona`/`StyleJson`/`PersonaComposer` type introduced by this story.
- [ ] A tenant can rank `claude`/`gemini`/`codegpt` as a reviewer (like-vs-like within a role) and drill into one persona's provider/model/promptVersion/`credentialSource` facets — on its own data only.
- [ ] Persona benchmark data is provably tenant-scoped (isolation suite green; platform admin denied per-tenant; the only cross-tenant view is 32-10's k-anonymous public-agent rollup).
- [ ] Incremental and full-rebuild persona rows are byte-identical; the per-tenant composite cursor never crosses tenants.
- [ ] No control-plane table added; `has-pending-model-changes` reports none; the `Tamma.Api.Tests` suite is green.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0 reframe table; §3.1 persona = named cross-role system agent; §3.4 disposition of 32-12 — rewrite + split style overlay to 32-19)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (story disposition + sequence)
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-12-persona-aware-benchmarking-plan.md`
- Prerequisites: `docs/stories/epic-32/story-32-10/32-10-benchmark-projections-and-leaderboards.md` (the projection + leaderboard + persona dimension + cursor + isolation this story extends), `docs/stories/epic-32/story-32-15/32-15-persona-reframe-and-seeding.md` (the persona-agent identity)
- Sibling stories: 32-6 (action trail), 32-8 (outcome/defect), 32-9 (usage/cost), 32-5 (call-LLM), 32-16 (enablement), 32-13 (dashboards), 32-14 (A/B), **32-19** (style/voice variants — the split-out style overlay; NOT a dependency)

## Logging Requirements

- **INFO**: persona projection fold completed (`tenantId, dimension:"persona", window, rowsUpdated, lastSequenceNumber`); persona leaderboard served (`tenantId, role, window, rankedCount, belowThresholdCount`); persona detail served (`tenantId, personaId, role, facetCount`).
- **DEBUG**: per-persona fold step (`personaId, role, facet sub-key, sequenceNumber`); facet bucketing (which facet, `unknown`-bucket yes/no — never the underlying value if sensitive).
- **WARN**: absent facet tag bucketed to `unknown` (`personaId, facet` — surfaced, not silently dropped); persona leaderboard requested with an unknown dimension/role; min-sample-suppressed persona row (`personaId, runCount, minRuns`).
- **ERROR**: malformed dimension key (`BENCHMARK.DIMENSION.INVALID`), projection-write failure after retries (the run is unaffected — 32-10 non-blocking contract), repository/migration failure.
- **Structured context**: include `{ tenantId, dimension:"persona", personaId, personaName, role, provider, model, promptRef, credentialSource, window, sequenceNumber, runCount }` where applicable.
- **Credential / privacy safety**: persona benchmarking is **credential-agnostic** — `credentialSource` is the **label** `byok`/`platform`/`unknown` only; **never** log, store, or surface a raw API key, prompt body, or `ConfigJson`. NEVER log a tenant id inside any cross-tenant path (there is none for persona benchmarks); the persona leaderboard payload is key-free and prompt-body-free by contract.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation — "persona = style/tone overlay within a role" (`atlas`/`nova`), a new `Persona` entity + `PersonaComposer` + style-aware benchmarking. | Claude |
| 2026-06-21 | 2.0.0   | **Vocabulary reframe to the locked model** (design §3.0/§3.4). "Persona" everywhere now = the **named cross-role public/system agent** (`claude`/`gemini`/`codegpt`) from sibling **32-15** — keyed by the persona-agent `AgentId`/`AgentName`, NOT a style overlay. The style/voice overlay (`atlas`/`nova` tone/verbosity) is **split out to new optional sibling 32-19 "Agent style/voice variants"** (a *variant*, not a persona) and all style-overlay-as-persona framing is removed. **No new entity** — the persona-aware benchmark now **rides 32-10's** existing `persona` dimension: refines its dimension key to the persona-agent identity, adds **provider/model/prompt-version/`credentialSource` facets**, and exposes the like-vs-like-within-a-role persona-comparison query. Per-tenant scoping (data ALWAYS tenant-scoped), the per-tenant `SequenceNumber` cursor rule, the no-CP-table/no-DROP-list/no-model-contract-churn gotchas, and the no-empty-fallback (explicit-`unknown` bucket) discipline are made explicit. The dropped `Persona` table, `PersonaComposer`, `StyleJson`, persona CRUD endpoints, and the persona-composition resolver path are removed (those belonged to the superseded style model / now 32-15 + 32-19). | Claude |
</content>
</invoke>
