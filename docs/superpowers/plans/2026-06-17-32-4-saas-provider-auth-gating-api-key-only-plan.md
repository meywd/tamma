# Story 32-4 — SaaS Provider Auth Gating (API-key only) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation.

**Story:** `docs/stories/epic-32/story-32-4/32-4-saas-provider-auth-gating-api-key-only.md`
**Epic:** 32 — Agents (first-class entities, managed execution, benchmarking, learning)
**Design spec:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§"Provider credential & auth model")

---

## Goal

Enforce the product invariant that **SaaS provider authentication is API-key only**. In SaaS mode,
CLI/token-based agent providers (`ICLIAgentProvider` — `claude-code`, `opencode`, …) can be neither
**selected** (agent create/version) nor **executed** (provider-chain resolver / managed entrypoint).
In single-user/self-hosted mode they remain fully usable. The gate is a pure function of
`(process mode, provider name)`, fail-closed, additive on top of the existing
`ProviderAllowlist`/`ActionGate` seams, and observable via a DCB event (`AGENT.PROVIDER.GATED`) +
an OTel counter (`tamma.provider.gated`).

## Non-goals (YAGNI guard)

- **NO new provider implementations** and **no change to `packages/providers`** — the TS provider
  hierarchies (`ILLMProvider` vs `ICLIAgentProvider`) are the *classification reference*, not edited.
  The C# side owns the gate.
- **NO change to single-user behaviour** — single-user is a hard no-op (no rejection, no event, no
  metric). Self-hosted Claude Code CLI usage is untouched.
- **NO new allowlist / no new mode plumbing / no new event store.** Reuse
  `ProviderAllowlist.DefaultProviders`, `ITammaModeProvider`, and `IEventRepository` respectively.
- **NO BYOK/platform credential resolution here** — that is Story 32-3. Gating runs *before*
  credential resolution and only inspects provider *names* (never secret material).
- **NO managed-execution layer here** — that is Story 32-5. This plan delivers the execution-boundary
  gate that 32-5 will call; it does not build `IManagedAgent`.
- **NO `packages/api`** — that package is DELETED. All work is in `apps/tamma-elsa` (C#).

## Current-state findings (verified 2026-06-17, repo @ main)

| Seam | Verified location | Relevance |
|---|---|---|
| Two provider hierarchies | `packages/providers/src/types.ts` — `ProviderCategory = 'llm-api' \| 'cli-agent'`; `ILLMProvider extends IProvider { type: 'llm-api' }` (L162); `ICLIAgentProvider extends IProvider { type: 'cli-agent' }` (L182) | Classification reference — `llm-api` ⇒ ApiKey, `cli-agent` ⇒ CliToken. |
| CLI agent provider names | `packages/providers/src/claude-agent-provider.ts` L80 `name = 'claude-code'`; `opencode-provider.ts` L69 `name = 'opencode'`; registered via `BUILTIN_PROVIDER_NAMES` (`agent-provider-factory.ts` L199) | The `CliToken` member set. Cross-check `BUILTIN_PROVIDER_NAMES` for extras (e.g. `zen-mcp`) at impl time. |
| Known-provider allowlist | `apps/tamma-elsa/src/Tamma.Activities/Security/ProviderAllowlist.cs` — `DefaultProviders` set (15 names: anthropic, openai, openrouter, google, github-copilot, local-llm, opencode, z-ai, zen-mcp, azure-openai, gemini, ollama, lmstudio, together, groq); `IsAllowedDefault(name)`; static `DefaultInstance` | **Reuse** as the single source of known providers; derive ApiKey set as `DefaultProviders \ CliTokenProviders`. |
| Sibling guardrail pattern | `apps/tamma-elsa/src/Tamma.Activities/Security/ActionGate.cs` — pure, fast, default-set + `IOptions` extension, `IsBlocked(cmd, out name)` | Pattern to mirror for the registry (pure, default-set, DI-friendly). |
| Process mode | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `enum TammaMode { SingleUser, SaaS }`; `ITammaModeProvider { TammaMode Mode }`; process-stable singleton | **Reuse** — the mode source; gate reads `.Mode` once. |
| Provider chain resolver | `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs` — `ResolveAsync(tenantId, role, action, options, ct)`; per-entry loop over `configured` handles with CB switch (L118-162); optional `IDiagnosticsService?` ctor overload (L45-58); returns `ChainResolveResult` with `EMPTY_PROVIDER_CHAIN`/`NO_AVAILABLE_PROVIDER` error codes | **Execution boundary.** Add `IProviderAuthRegistry?` + `ITammaModeProvider` via the same optional-ctor pattern; skip ineligible entries pre-CB-switch. |
| Chain types | `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainTypes.cs` — `ChainEntry`, `ChainReason` (Healthy/Unknown/HalfOpenProbeCandidate/CircuitOpen), `ProviderHandle`, `ChainResolveResult` | Add `ChainReason.SaaSIneligible`. |
| Agent selection seam | `apps/tamma-elsa/src/Tamma.Api/Services/Agents/` (`AgentResolverService`, `IAgentResolverService`, `ResolvedAgentConfig`, `DefaultAgentConfig`); `AgentRegistryService.cs` does **NOT** exist yet (NEW from 32-2); create/version write in `Endpoints/AgentEndpoints.cs` (`configRepo.UpsertAsync`, ~L89) | **Selection boundary.** Hook `AgentRegistryService` if 32-2 merged; otherwise wire `AgentEndpoints` create/version. |
| DCB event emission | `Endpoints/AgentEndpoints.cs` L93 `await events.AppendAsync(new DomainEvent { Type=..., TenantId, Tags, Metadata, Data, CreatedAt })`; `IEventRepository.AppendAsync` (`Tamma.Data/Repositories/IEventRepository.cs`); `DomainEvent` entity (`Tamma.Data/Entities/DomainEvent.cs`) | **Reuse** exact pattern for `AGENT.PROVIDER.GATED`. |
| Error type | `apps/tamma-elsa/src/Tamma.Core/TammaError.cs` — `TammaError(code, message, context?, retryable=false, severity=Medium)`; `TammaErrorSeverity` | Throw `SAAS_PROVIDER_NOT_ALLOWED` at selection; existing endpoint `TammaError`→400 mapping produces the response. |
| Metrics pattern | `KekRotationMetrics` (per MEMORY: `tamma.kek_rotation.remaining` OTel gauge) | Mirror for `tamma.provider.gated` counter. |
| Existing AGENT.* events | `AGENT.DISPATCH.STARTED/SUCCESS/FAILED`, `AGENT.RESULTS.PARTIAL`, `AGENT_CONFIG.UPDATED.SUCCESS` | `AGENT.PROVIDER.GATED` is new, follows the `AGGREGATE.ACTION.STATUS` convention. |

**Test execution:** C# docker-bound suites run `sg docker -c "dotnet test ..."` (session docker group
is stale — `reference_dotnet_test_docker.md`); build needs no wrapper.

---

## Phased tasks

### Phase 1 — Provider auth registry (`IProviderAuthRegistry`)

The pure classification layer. Lives in `Tamma.Activities/Security/` beside `ProviderAllowlist` so
engine activities can reuse it without an Api dependency.

- [ ] **Test first:** `tests/Tamma.Activities.Tests/Security/ProviderAuthRegistryTests.cs`
  - every `ProviderAllowlist.DefaultProviders` entry returns a non-null `AuthModel` (no
    silent miscategorisation);
  - `claude-code`, `opencode` ⇒ `CliToken`, `IsSaaSEligible == false`;
  - `anthropic`, `openai`, `openrouter`, `gemini`, `google`, `github-copilot`, `azure-openai`,
    `local-llm`, `ollama`, `lmstudio`, `together`, `groq`, `z-ai` ⇒ `ApiKey`, eligible;
  - unknown name ⇒ `AuthModel == null`, `IsSaaSEligible == false` (fail-closed);
  - null/whitespace ⇒ `null`/`false`; case-insensitivity.
- [ ] Implement `IProviderAuthRegistry.cs` (enum `ProviderAuthModel { ApiKey, CliToken }`,
  interface) and `ProviderAuthRegistry.cs` (derive ApiKey = `DefaultProviders \ CliTokenProviders`;
  `CliTokenProviders = { claude-code, opencode }` + any extra CLI registrations found in
  `BUILTIN_PROVIDER_NAMES`).
- [ ] **Files:** create both under `apps/tamma-elsa/src/Tamma.Activities/Security/`.

**Done when:** registry tests green; the "every allowlist entry classifies" test passes (guards
future provider additions).

### Phase 2 — The gate (`ISaaSProviderGate`) + event + metric

Api-side because it needs `ITammaModeProvider` and emits DCB events.

- [ ] **Test first:** `tests/Tamma.Api.Tests/Security/SaaSProviderGateTests.cs`
  - single-user: every provider ⇒ `Allowed`, **zero** events, **zero** metric increments;
  - SaaS: `ApiKey` ⇒ `Allowed`, no event; `CliToken` ⇒ denied, **exactly one**
    `AGENT.PROVIDER.GATED` event (assert `Type`, `Data.provider/authModel/mode/reason`, `Tags`,
    `TenantId`), **exactly one** counter increment;
  - SaaS unknown ⇒ denied (fail-closed);
  - `EnsureAllowedAsync` throws `TammaError("SAAS_PROVIDER_NOT_ALLOWED", severity High)` with
    `Context.provider` set; event append failure is swallowed (decision/throw still returns).
- [ ] Implement `ProviderGateContext`/`ProviderGateDecision` records, `ISaaSProviderGate`,
  `SaaSProviderGate` (`InspectAsync` short-circuits single-user; SaaS denial emits event + metric;
  `EnsureAllowedAsync` calls `InspectAsync` then throws on deny).
- [ ] Implement `ProviderGatingMetrics` (Meter + `Counter<long> tamma.provider.gated`, tags
  `provider`/`auth_model`/`reason`) mirroring `KekRotationMetrics`.
- [ ] **Files:** create `ISaaSProviderGate.cs`, `SaaSProviderGate.cs`, `ProviderGatingMetrics.cs`
  under `apps/tamma-elsa/src/Tamma.Api/Services/Security/`.

**Done when:** gate tests green incl. the zero-side-effect single-user assertions and the
event-append-failure-is-swallowed assertion.

### Phase 3 — Execution boundary (resolver fail-closed backstop)

- [ ] **Test first:** `tests/Tamma.Api.Tests/Providers/ProviderChainResolverSaaSGatingTests.cs`
  - SaaS chain `[claude-code, anthropic]` ⇒ `claude-code` in `Skipped` w/ `ChainReason.SaaSIneligible`,
    `anthropic` recommended;
  - SaaS chain `[claude-code]` ⇒ `ErrorCode == "SAAS_PROVIDER_NOT_ALLOWED"`, `AllExhausted`,
    one `AGENT.PROVIDER.GATED` event;
  - single-user: both chains resolve `claude-code` normally, no skip-for-gating, no event;
  - regression: existing `EMPTY_PROVIDER_CHAIN` / `NO_AVAILABLE_PROVIDER` / budget / CB tests
    still pass unchanged.
- [ ] Add `ChainReason.SaaSIneligible` to `ProviderChainTypes.cs`.
- [ ] Modify `ProviderChainResolver`: add optional-ctor overload injecting `IProviderAuthRegistry?`
  + `ITammaModeProvider?` (mirror the existing `IDiagnosticsService?` optional pattern so existing
  unit tests don't all need rework); in the per-entry loop, before the CB switch, when
  `Mode == SaaS && !registry.IsSaaSEligible(handle.Provider)` add to `skipped` with
  `SaaSIneligible` and `continue`; when the ordered set is empty *due to* gating, return
  `ErrorCode "SAAS_PROVIDER_NOT_ALLOWED"` and call the gate once (for the event/metric).
- [ ] **Files:** modify `ProviderChainResolver.cs`, `ProviderChainTypes.cs`.

**Done when:** resolver gating tests green AND every pre-existing resolver test still green.

### Phase 4 — Selection boundary

- [ ] **Test first:** `tests/Tamma.Api.Tests/Agents/AgentSelectionGatingTests.cs`
  - SaaS create/version with a `CliToken` provider in the chain ⇒ **400** `SAAS_PROVIDER_NOT_ALLOWED`,
    config **not** persisted, one event;
  - SaaS all-`ApiKey` chain ⇒ persists;
  - single-user `CliToken` chain ⇒ persists, no event.
- [ ] Wire `ISaaSProviderGate.EnsureAllowedAsync` into the agent create/version write — call for the
  primary + every fallback provider in the submitted config **before** `UpsertAsync`. Prefer
  `AgentRegistryService` (32-2) if merged; else attach to `AgentEndpoints` create/version (~L89) and
  leave a `// TODO(32-2): move to AgentRegistryService` hook.
- [ ] **Files:** modify `AgentEndpoints.cs` (and/or `AgentRegistryService.cs` if present).

**Done when:** selection tests green; rejected configs are provably absent from `agent_configs`.

### Phase 5 — DI wiring + mode-matrix integration test + full-suite gate

- [ ] Register in `Program.cs`: `IProviderAuthRegistry → ProviderAuthRegistry` (singleton),
  `ISaaSProviderGate → SaaSProviderGate` (scoped — emits tenant-scoped events), `ProviderGatingMetrics`
  (singleton); pass registry + mode into `ProviderChainResolver` registration.
- [ ] **Mode-matrix integration test** (parameterised): `(mode ∈ {SingleUser, SaaS}) ×
  (provider ∈ {anthropic, claude-code, unknown})` over both boundaries → assert the 12-cell
  allow/deny/event/metric matrix.
- [ ] Run the full C# suite via `sg docker -c "dotnet test ..."`; confirm green and
  `has-pending-model-changes` reports none (no EF migration in this story — no schema change).

**Done when:** full suite green; matrix test passes; no migration drift.

---

## Sequencing

```
Phase 1 (registry, pure) ─▶ Phase 2 (gate + event + metric)
                                   │
                   ┌───────────────┴───────────────┐
                   ▼                                ▼
            Phase 3 (resolver)              Phase 4 (selection)   ← parallel-safe after Phase 2
                   └───────────────┬───────────────┘
                                   ▼
                          Phase 5 (DI + matrix + full suite)
```

Phase 1 is the only hard prerequisite for everything. Phases 3 and 4 are independent once Phase 2
lands and may be split across subagents. Phase 5 closes the wave.

## Risks

- **CLI-provider enumeration drift.** If `BUILTIN_PROVIDER_NAMES` registers a CLI agent beyond
  `claude-code`/`opencode` (e.g. `zen-mcp` is in the allowlist), missing it would let a CLI provider
  through in SaaS. *Mitigation:* the Phase-1 "every allowlist entry has a deterministic auth model"
  test forces an explicit decision per provider; cross-check the TS factory at impl time.
- **Resolver regression.** `ProviderChainResolver` is load-bearing (Story 9-5 budget, CB). *Mitigation:*
  null-tolerant optional-ctor (single-user / no-registry ⇒ untouched behaviour); Phase-3 includes a
  full regression pass on existing resolver tests.
- **Event-store failure masking the deny.** An `AppendAsync` failure must never turn a clean 400 into
  a 500 or let a CLI provider slip through. *Mitigation:* event emission is fire-and-forget (logged,
  swallowed); the deny decision is independent of the append. Asserted in Phase 2.
- **32-2 timing.** `AgentRegistryService` may not be merged when this lands. *Mitigation:* selection
  gate attaches to the existing `AgentEndpoints` write with a documented hook; the execution-boundary
  gate (the security-critical backstop) ships fully regardless of 32-2.
- **Double-emission.** Both boundaries could emit `AGENT.PROVIDER.GATED` for one logical request
  (select then resolve). *Mitigation:* acceptable by design (distinct boundaries, distinct
  reasons/tags); tests assert *exactly one* event *per boundary call*, not per request.
- **Single-user pollution.** A bug making the gate active in single-user would break self-hosted CLI
  usage. *Mitigation:* `Mode == SingleUser` short-circuit is the first line of `InspectAsync`; tests
  assert zero events/metrics in single-user.

## Acceptance criteria (plan-level — maps 1:1 to story ACs)

- [ ] `IProviderAuthRegistry` classifies every allowlist provider ApiKey/CliToken; unknown ⇒
  ineligible (fail-closed). *(Story AC1, AC6)*
- [ ] SaaS create/version/select of a `CliToken` provider ⇒ 400 `SAAS_PROVIDER_NOT_ALLOWED`, named
  provider, not persisted. *(AC2)*
- [ ] Single-user allows `CliToken` providers with zero gating side effects. *(AC3)*
- [ ] Resolver + managed entrypoint skip/deny SaaS-ineligible providers, fail closed, emit
  `AGENT.PROVIDER.GATED`. *(AC4)*
- [ ] Mode read once from `ITammaModeProvider`; gate is pure `(mode, provider)`. *(AC5)*
- [ ] `ProviderAllowlist`/`ActionGate`/`ITammaModeProvider`/`IEventRepository` reused, not
  duplicated; logic additive + fail-closed. *(AC6)*
- [ ] `AGENT.PROVIDER.GATED` emitted with `{provider, authModel, mode, reason}` (+ role/action);
  `tamma.provider.gated` counter incremented. *(AC7, AC8)*
- [ ] Tests cover SaaS-reject-at-selection, SaaS-reject-at-execution, single-user-allow,
  api-key-passes-both-modes, unknown-denied; full C# suite green, no migration drift. *(AC9)*
