# Story 32-4 — SaaS Provider Gate (call-LLM endpoint stage)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21
**Goal:** Deliver `ISaaSProviderGate` — the **gate stage** (composition step 1) of the 32-5 `call-LLM`
endpoint (`POST /api/v1/llm/call`). In SaaS mode it denies `cli-token` (harness) providers and unknown
providers fail-closed (typed → HTTP 400 `SAAS_PROVIDER_NOT_ALLOWED`), denies un-entitled tenants
(typed → HTTP 403), and passes entitled `api-key` providers. In single-user/self-hosted mode it is a
hard no-op (harness providers are a legitimate local affordance). The gate returns a **typed
`ProviderGateDecision`** the endpoint maps to the design §2.4 envelope — it never throws a bare
exception. Eligibility is sourced from 34-11's `Provider.AuthModel` via `IProviderAuthLookup`, with a
`StaticProviderAuthLookup` interim impl until 34-11 lands.

**Story file:** `docs/stories/epic-32/story-32-4/32-4-saas-provider-auth-gating-api-key-only.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`
(§2.4 error/gating, §2.6 step 1 gate stage, §4.2 `AuthModel` → SaaS-eligibility)
**Deep dive:** `docs/superpowers/specs/2026-06-20-managed-llm-execution-deep-dive.md` §1 (provider duality)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api`). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). `packages/api` is DELETED — all of this is C#.

---

## Reframe context (why this is a v2 rewrite)

The v1 story was standalone two-seam gating: `IProviderAuthRegistry` (in `Tamma.Activities`) +
a selection hook in `AgentEndpoints` create/version + an execution-boundary skip in
`ProviderChainResolver` (`ChainReason.SaaSIneligible`). The locked model (§0–§2) mediates the LLM path
through one endpoint, so the provider-chain/resolver concerns move server-side into 32-5's
`ManagedAgent`. **This plan drops the resolver-skip + selection-endpoint hooks entirely** and delivers
a single `ISaaSProviderGate` service that the 32-5 endpoint calls as step 1. Result: smaller surface,
one seam, typed decision → §2.4 envelope.

---

## Non-goals (YAGNI guard)

- **NO resolver-skip logic.** `ProviderChainResolver` is NOT touched — the chain/retry concern is
  32-5's, server-side. No `ChainReason.SaaSIneligible`.
- **NO selection-endpoint hook.** `AgentEndpoints` create/version is NOT modified — selection
  constraint is enablement (32-16/32-18), not provider-auth gating.
- **NO `Provider` entity / `AuthModel` column / EF migration.** 34-11 owns those. This story defines
  only the `IProviderAuthLookup` read seam + the interim static impl. **No migration is added here**,
  so the `Program.cs` startup-reset DROP-list is untouched (that note applies only to new tables).
- **NO entitlement engine.** The 403 path delegates to the existing Epic 34 SaaS auth/entitlement
  seam; this story surfaces its result as a typed `Outcome`, it does not implement entitlement rules.
- **NO bare throw on denial.** Denials are typed `ProviderGateDecision`; only a contract violation
  (null context) may throw. The endpoint (32-5) maps the decision to the envelope.
- **NO credential / secret access.** The gate touches provider names + mode only.

---

## Current-state findings (verify at impl time, repo @ feat/exec-wave-02)

| Seam | Where it is today | How 32-4 uses it |
|---|---|---|
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (`SingleUser`\|`SaaS`), process-stable. | Read once at the top of `InspectAsync`; `!= SaaS` ⇒ no-op `Allow`. |
| **Known-provider set (interim)** | `Tamma.Activities/Security/ProviderAllowlist.cs` — `DefaultProviders` + `IsAllowedDefault(name)`. | `StaticProviderAuthLookup` derives `api-key = DefaultProviders \ CliTokenProviders`. |
| **CLI-agent provider names** | `packages/providers` — `claude-agent-provider.ts` (`claude-code`), `opencode-provider.ts` (`opencode`), zen-mcp; registered via `BUILTIN_PROVIDER_NAMES` / `agent-provider-factory.ts`. | The interim `CliTokenProviders` set; cross-check at impl time. |
| **DCB events** | `Tamma.Data/Repositories/IEventRepository.cs` — `Task<DomainEvent> AppendAsync(DomainEvent)`, tenant-scoped; pattern in `AgentEndpoints` (`AGENT_CONFIG.UPDATED.SUCCESS`, ~line 93). | Emit `AGENT.PROVIDER.GATED` on SaaS denial; swallow append failure. |
| **OTel metrics** | `KekRotationMetrics` (`Meter` + `Counter<long>`) pattern. | New `ProviderGatingMetrics` → `Counter<long> tamma.provider.gated`. |
| **SaaS auth / entitlement** | Epic 34 gating seam (entitlement check for tenant × managed-LLM path). | Delegated for the 403 `TenantNotEntitled` outcome; injected behind a thin interface, faked in tests. |
| **34-11 AuthModel** | `Provider.AuthModel` (`api-key`\|`cli-token`) — design §4.2; NOT yet built. | `EntityProviderAuthLookup` (34-11) backs `IProviderAuthLookup`; interim static impl until then. |
| **Consumer** | 32-5 `ManagedAgent.RunAsync` step 1 / `LlmCallEndpoints.cs`. | Calls `InspectAsync`, maps `Outcome`+`HttpStatusHint` → §2.4 envelope. |

**Key insight:** the only genuinely new code is one service (`SaaSProviderGate`), one read seam +
interim impl (`IProviderAuthLookup` / `StaticProviderAuthLookup`), one metrics class, one DI block,
and the tests. No EF, no resolver edits, no endpoint edits (the endpoint is 32-5's).

---

## Architecture

```
POST /api/v1/llm/call  (32-5: LlmCallEndpoints -> ManagedAgent.RunAsync)
   │  step 1
   ▼
ISaaSProviderGate.InspectAsync(ProviderGateContext{ provider, role?, action?, tenantId? })
   │
   ├─ mode = ITammaModeProvider.Mode
   │     != SaaS  ──────────────────────────────────► Allow (no lookup, no event, no metric)
   │
   └─ SaaS:
        authModel = IProviderAuthLookup.AuthModelAsync(provider)   // 34-11 entity / interim static
          null (unknown)  ─► EmitGated(PROVIDER_UNKNOWN)   ─► Deny  Outcome=SaasProviderNotAllowed (400)
          CliToken        ─► EmitGated(CLI_TOKEN_PROVIDER) ─► Deny  Outcome=SaasProviderNotAllowed (400)
          ApiKey:
            entitled? (Epic 34 seam)
              false ─► EmitGated(TENANT_NOT_ENTITLED)      ─► Deny  Outcome=TenantNotEntitled (403)
              true  ─────────────────────────────────────► Allow
   │
   ▼  ProviderGateDecision (typed)
32-5 endpoint maps: SaasProviderNotAllowed→400 SAAS_PROVIDER_NOT_ALLOWED ; TenantNotEntitled→403
```

Per-mode ownership (CLAUDE.md two-scoping-model): single-user = hard no-op, no events (harness
providers legitimate locally); SaaS = load-bearing, tenant-scoped `AGENT.PROVIDER.GATED` events in the
tenant `t_<hex>` store, eligibility from the platform-global `Provider.AuthModel`. Mode from
`ITammaModeProvider`.

---

## Task breakdown

Order: T1 (lookup seam + interim impl) → T2 (gate contract records) → T3 (gate core + no-op/SaaS
branches) → T4 (events + metric) → T5 (DI wiring + 34-11 swap-readiness) → T6 (matrix + contract +
credential-safety tests). T1 and T2 are parallel-safe; T3 needs both.

### T1 — `IProviderAuthLookup` + `StaticProviderAuthLookup` (interim 34-11 seam)

**Scope:** The single eligibility read seam. Interim static impl keyed off `ProviderAllowlist.DefaultProviders`.

**Files (new):** `Services/Security/IProviderAuthLookup.cs`, `Services/Security/StaticProviderAuthLookup.cs`
(+ the `ProviderAuthModel` enum, co-located in `IProviderAuthLookup.cs` or `ISaaSProviderGate.cs`).

**Tests (first):** `tests/Tamma.Api.Tests/Security/StaticProviderAuthLookupTests.cs`
- every `ProviderAllowlist.DefaultProviders` entry resolves to a non-null `AuthModel` (guards a new
  provider being silently mis-classified);
- `claude-code` / `opencode` / `zen-mcp` ⇒ `CliToken`;
- `anthropic` / `openai` / `openrouter` / `gemini` ⇒ `ApiKey`;
- unknown name ⇒ `null`;
- case-insensitivity + trimming (`"Claude-Code "` ⇒ `CliToken`).

**Acceptance:**
- [ ] Interim lookup derives `api-key = DefaultProviders \ {claude-code,opencode,zen-mcp}`; does not
      re-list providers.
- [ ] Builds clean; no analyzer warnings.

### T2 — Gate contract records (`ProviderGateContext`, `ProviderGateDecision`, enums)

**Scope:** The typed surface the endpoint consumes. No behaviour.

**Files (new):** `Services/Security/ISaaSProviderGate.cs` (interface + `ProviderGateContext`,
`ProviderGateDecision`, `ProviderGateOutcome`; `ProviderAuthModel` if not in T1).

**Tests (first):** `tests/Tamma.Api.Tests/Security/ProviderGateDecisionTests.cs` — record equality;
`Allow(model)` factory ⇒ `Allowed=true, Outcome=Allowed, Reason=null, HttpStatusHint=200`; a denial
record carries a non-null `Reason` + the right `HttpStatusHint` (400/403).

**Acceptance:**
- [ ] `ProviderGateDecision` has `{ Allowed, Outcome, Reason?, AuthModel?, HttpStatusHint }`.
- [ ] `ProviderGateOutcome` has `Allowed | SaasProviderNotAllowed | TenantNotEntitled`.

### T3 — `SaaSProviderGate.InspectAsync` core (mode no-op + SaaS branches)

**Scope:** `SaaSProviderGate : ISaaSProviderGate`. Single-user ⇒ `Allow` (no lookup). SaaS ⇒ classify
→ `cli-token`/unknown deny (400), `api-key` + not-entitled deny (403), else allow. **No throw on
denial.** Inject `ITammaModeProvider`, `IProviderAuthLookup`, the entitlement seam, `IEventRepository`,
`ProviderGatingMetrics`, `ILogger<SaaSProviderGate>`. (Events/metric wired fully in T4 but the call
sites land here.)

**Files (new):** `Services/Security/SaaSProviderGate.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs` (branch coverage; events
asserted in T4):
- single-user: `claude-code` / `anthropic` / unknown ⇒ `Allowed`, no lookup consulted (assert the
  fake lookup is never called), zero side effects;
- SaaS `anthropic` + entitled ⇒ `Allowed`;
- SaaS `claude-code` ⇒ denied `SaasProviderNotAllowed`, `HttpStatusHint=400`;
- SaaS unknown ⇒ denied `SaasProviderNotAllowed`, `HttpStatusHint=400` (fail-closed);
- SaaS `anthropic` + not-entitled ⇒ denied `TenantNotEntitled`, `HttpStatusHint=403`;
- no path throws (assert `InspectAsync` never throws for any valid context).

**Acceptance:**
- [ ] Mode short-circuit is first (no lookup in single-user).
- [ ] All four SaaS outcomes produce the correct `Outcome` + `HttpStatusHint`; none throw.

### T4 — `AGENT.PROVIDER.GATED` event + `tamma.provider.gated` metric (AC8)

**Scope:** `EmitGatedAsync(ctx, authModel, reason, ct)` — append one `AGENT.PROVIDER.GATED` event via
the tenant `IEventRepository` (Data/Tags per the story) and increment `tamma.provider.gated` once;
**swallow** append failure (log ERROR, never rethrow). Wire it into each SaaS-denial branch.
`ProviderGatingMetrics` is a new `Meter`+`Counter<long>` class (mirror `KekRotationMetrics`).

**Files (new):** `Services/Security/ProviderGatingMetrics.cs`. **Modify:** `SaaSProviderGate.cs`
(call `EmitGatedAsync` in the three denial branches).

**Tests (first):** extend `SaaSProviderGateTests`:
- each SaaS denial (cli-token / unknown / not-entitled) emits **exactly one** `AGENT.PROVIDER.GATED`
  event (fake `IEventRepository` records appends) with the right `Data.reason`
  (`CLI_TOKEN_PROVIDER` / `PROVIDER_UNKNOWN` / `TENANT_NOT_ENTITLED`), `authModel`, `mode=saas`,
  tenant-scoped `Tags`, and **exactly one** counter increment (tags `provider`, `auth_model`, `reason`);
- an `Allowed` decision emits zero events / zero increments;
- single-user emits zero events / zero increments;
- event-append failure (fake repo throws) is swallowed — `InspectAsync` still returns the typed
  decision, ERROR logged.

**Acceptance:**
- [ ] Exactly one event + one metric per SaaS denial; zero on allow / single-user.
- [ ] Append failure never escapes `InspectAsync`.

### T5 — DI wiring + 34-11 swap-readiness (AC7)

**Scope:** Register `ISaaSProviderGate → SaaSProviderGate`, `IProviderAuthLookup →
StaticProviderAuthLookup`, `ProviderGatingMetrics` in `Program.cs` (mirror existing service
registrations). Document the one-line swap to `EntityProviderAuthLookup` when 34-11 lands.

**Files:** modify `Tamma.Api/Program.cs`. (No `Tamma.Activities` change; no Elsa activity.)

**Tests (first):** a host smoke test (`WebApplicationFactory`) resolves `ISaaSProviderGate` and
`IProviderAuthLookup` at startup. (If a full host boot is heavy, assert the DI registrations via the
service-collection directly.)

**Acceptance:**
- [ ] DI resolves the gate + lookup + metrics at host startup.
- [ ] The swap to `EntityProviderAuthLookup` (34-11) is a single registration line — documented in a
      `Program.cs` comment next to the registration.

### T6 — Matrix, endpoint-mapping contract, 34-11 swap, credential-safety tests (AC10)

**Scope:** The canonical regression guards.

**Files (new):** `tests/Tamma.Api.Tests/Security/SaaSProviderGateMatrixTests.cs`.

**Tests:**
- **Mode × auth-model × entitlement matrix**: `(mode ∈ {SingleUser, SaaS}) × (provider ∈ {anthropic,
  claude-code, unknown}) × (entitled ∈ {true,false})` → assert `Allowed`/`Outcome`/`HttpStatusHint`/
  event-count/metric-count for each cell.
- **Endpoint-mapping contract** (referenced by 32-5; assert at the decision level here): a denied
  decision with `Outcome=SaasProviderNotAllowed` ⇒ `HttpStatusHint=400`; `TenantNotEntitled` ⇒ 403 —
  proving the typed decision drives the §2.4 envelope.
- **34-11 swap**: register `EntityProviderAuthLookup` (fake `Provider` rows: `anthropic`=api-key,
  `claude-code`=cli-token) and re-run the matrix — passes unchanged (contract-neutral swap).
- **Credential-safety**: assert `SaaSProviderGate`'s constructor dependencies contain **no**
  credential-resolver / secret seam; a context carrying only a provider name produces a decision
  without any secret access.

**Acceptance:**
- [ ] Matrix passes for every cell; SaaS never allows `cli-token`/unknown; single-user allows all.
- [ ] 34-11 swap test passes (DI-only change is contract-neutral).
- [ ] Credential-safety assertion holds.

---

## Story order & dependencies

External: **34-11** (`Provider.AuthModel`) — *soft* prereq (interim static impl until it lands; swap
is DI-only). **32-5** (call-LLM endpoint) is the **consumer**, implemented immediately after this gate.
**32-3** credential model presumed by the `api-key` classification (gate runs before credential
resolution). Reuses `ITammaModeProvider`, `ProviderAllowlist`, `IEventRepository`, the Epic 34
entitlement seam. Internal: T1 ∥ T2 → T3 → T4 → T5 → T6.

**EF / migration note:** this story adds **no** EF entity or migration, so it does not amend the
single migration snapshot and does not touch the `Program.cs` startup-reset DROP-list (the `Provider`
table is 34-11's). Implemented sequentially with the rest of the wave regardless.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Security"
# confirm no resolver / selection-endpoint edits crept in (v1 topology must NOT return)
grep -rn "SaaSIneligible\|EnsureAllowedAsync" apps/tamma-elsa/src || echo "OK: no v1 seams"
# confirm the gate has no credential dependency
grep -n "CredentialResolver\|ITenantProviderKeyReader\|cabinet" apps/tamma-elsa/src/Tamma.Api/Services/Security/SaaSProviderGate.cs || echo "OK: gate is secret-free"
```

## Risks

- **`cli-token` leak into SaaS** (High): gate is step 1 of the endpoint, before credential/provider
  call; explicit SaaS+`cli-token` test asserts 400 and no credential resolution.
- **Unknown provider silently allowed** (High): fail-closed `null` ⇒ DENY; lookup test asserts every
  known provider resolves deterministically.
- **34-11 not landed** (Medium): interim `StaticProviderAuthLookup`; swap is DI-only with a
  contract-neutral test.
- **Bare throw → leaked 500** (Medium): typed decision only; event-append failure swallowed; only a
  null context may throw.
- **403 entitlement drift from Epic 34** (Medium): delegate entitlement to the Epic 34 seam; surface
  only its result as `Outcome=TenantNotEntitled`.
- **v1 topology resurfacing** (Low): `grep` guard in Verification ensures no resolver-skip /
  `EnsureAllowedAsync` returns.
