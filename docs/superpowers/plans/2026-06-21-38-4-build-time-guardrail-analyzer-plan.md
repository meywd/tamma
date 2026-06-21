# Story 38-4 — Build-Time Guardrail Analyzer (rule-1 enforcement) — Implementation Plan

> **Date:** 2026-06-21
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — write the analyzer test
> fixtures (positive / negative / exempt) BEFORE the analyzer.

**Goal:** A permanent, build-failing backstop for the "steps never call external APIs directly" rule
(design §0/§1) across Epics 32 + 38. A Roslyn `DiagnosticAnalyzer` (**`TAMMA001`**, severity **Error**)
plus a reflection-backed architecture test that **fail the build** if any class under the engine
surface (`Tamma.Activities` / `Tamma.ElsaServer`) references `HttpClient`/`PostAsync`/`PostAsJsonAsync`/
`SendAsync` to a non-`TammaApiClient`, non-`Engine:CallbackUrl` host, or injects a credential-holding
vendor service (Octokit / `IGitHubActionsClient` / Slack/Stripe client / vendor `IIntegrationService`
members / raw `IProviderCredentialResolver`). So a re-introduced direct external call can never compile.

**Story file:** `docs/stories/epic-38/story-38-4/38-4-build-time-guardrail-analyzer.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1, §1.2, §5.2, §5.3)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa`. Solution `Tamma.sln`. Central props
`Directory.Build.props` (`TreatWarningsAsErrors=false`, `EnforceCodeStyleInBuild=true`). CI:
`.github/workflows/ci.yml` runs `dotnet build Tamma.sln --no-restore -c Release` then
`dotnet test Tamma.sln` with `working-directory: apps/tamma-elsa`. The analyzer is a separate
`netstandard2.0` project referenced as an `Analyzer`. Tests via `sg docker -c "dotnet test ..."` where
docker-bound; the **primary check here is `dotnet build`** (needs no wrapper). **`packages/api` is
DELETED — all C#.**

---

## Non-goals (YAGNI guard)

- **NO fixing of current violations.** That is the cutover work of 32-5 / 38-1 / 38-2 / 38-3. This
  story is the **backstop**; sequence it **last** so the engine surface is already clean.
- **NO enforcement on `Tamma.Api`.** The API is *supposed* to hold credentials and call vendors — the
  analyzer's `CompilationStartAction` early-returns unless the assembly is the engine surface.
- **NO flagging of the §5.3 exempt categories.** `ICLIAgentProvider`, local tools
  (`FileReadTool`/`ShellExecuteTool`/`GitOperationsTool`), and inbound webhook receivers
  (`WebhookSignalRegistry`) are local/inbound, not external API calls — they must NOT trip `TAMMA001`.
- **NO runtime footprint.** No DB table, no EF migration, no CP entity → no `Program.cs` DROP-list
  entry, no `ControlPlaneDbContextModelTests` change.
- **NO Stripe/billing code.** The denylist is forward-compatible (add the Stripe type when Epic 35
  references the SDK), but this story writes no billing code.

---

## Current-state findings (verified 2026-06-21, `feat/exec-wave-02`)

| Fact | Detail | Plan impact |
|---|---|---|
| **No existing analyzer infra** | `grep -rl "DiagnosticAnalyzer\|Microsoft.CodeAnalysis" *.csproj` → none. Clean slate. | New `Tamma.Activities.Guardrails` analyzer project from scratch. |
| **Solution + projects** | `apps/tamma-elsa/Tamma.sln`; engine surface = `src/Tamma.Activities` + `src/Tamma.ElsaServer`; `src/Tamma.Api` is the allowed-to-call-vendors side. | Reference the analyzer from the two engine projects only. |
| **Central props** | `Directory.Build.props`: `TreatWarningsAsErrors=false`, `EnforceCodeStyleInBuild=true`, `AnalysisLevel=latest`. | Declare `TAMMA001` at `DiagnosticSeverity.Error` (a `Warning` would NOT fail the build). |
| **CI gate** | `.github/workflows/ci.yml`: `dotnet build Tamma.sln --no-restore -c Release` then `dotnet test Tamma.sln`, `working-directory: apps/tamma-elsa`. | The build step is the gate — analyzer-as-Error fails it; **no new CI job needed**. |
| **Sanctioned seams** | `TammaApiClient` (`Tamma.Activities/LlmCall/TammaApiClient.cs`, Bearer `Tamma:ApiToken` + `X-Tenant-Id`); `TriggerCIActivity` (`Tamma.Activities/Testing/TriggerCIActivity.cs`, POST to `Engine:CallbackUrl/api/engine/trigger-ci`); `QueueWelcomeEmailActivity` (outbox — no engine-side external call). | Encode these in the allowlist; assert no false positive on them. |
| **Vendor denylist source** | design §1.2 audit table: `Octokit`/`IGitHubIntegrationService`, `IGitHubActionsClient`, `IIntegrationService` Slack/GitHub members, `IProviderCredentialResolver`, (future) Slack/Stripe clients. | The denylist is data derived directly from the table. |
| **Exempt categories** | design §5.3: `ICLIAgentProvider` (local process), local tools, `WebhookSignalRegistry` (inbound). | Encode as `ExemptBaseTypes`; assert no diagnostic. |

**Key insight:** the only new code is one analyzer (two detection passes — invocation + symbol), the
allow/deny/exempt data, the analyzer unit tests with fixtures, the reflection backstop, and the
project-reference wiring. No runtime code, no DB.

---

## Architecture

```
dotnet build Tamma.sln -c Release  (CI gate — ci.yml)
        |
        v
Tamma.Activities / Tamma.ElsaServer  --(ProjectReference OutputItemType="Analyzer")-->  Tamma.Activities.Guardrails
        |                                                                                       |
        |  every type in the engine-surface compilation                                        v
        |                                                                  EngineExternalCallAnalyzer
        |                                                                    (a) InspectInvocation: HttpClient.Post*/Send*/Get*
        |                                                                        target host ∉ {TammaApiClient, Engine:CallbackUrl}? -> TAMMA001 (Error)
        |                                                                    (b) InspectConstructor/Field: type ∈ VendorDenylist? -> TAMMA001 (Error)
        |                                                                    exempt: ICLIAgentProvider / local tools / WebhookSignalRegistry
        v
build FAILS on TAMMA001  <-- a re-introduced direct external call can never compile

dotnet test Tamma.sln
        |
        +-- EngineExternalCallAnalyzerTests   (Roslyn analyzer test cases: positive/negative/exempt)
        +-- ActivitiesGuardrailTests          (reflection backstop on real Tamma.Activities + positive-control proof analyzer is wired)
```

**Mode-independent:** build-time only; no `ITammaModeProvider`, no tenant/user scoping, no request
principal. (The rule it enforces is *why* both modes stay safe, but the check is the same build for both.)

---

## Task breakdown

Order: T1 (analyzer project + descriptor + allowlist data) → T2 (invocation pass) → T3 (injection
pass) → T4 (exemptions + non-engine-surface skip) → T5 (project-reference wiring + sln) → T6
(reflection backstop + suppression-resistance). TDD: each detection task writes its fixtures first.

### T1 — Analyzer project scaffold + `TAMMA001` descriptor + allow/deny data

**Scope:** The `netstandard2.0` analyzer project, the `DiagnosticDescriptor` (Error severity), and the
allowlist/denylist/exempt data. No detection logic yet.

**Files (new):** `src/Tamma.Activities.Guardrails/Tamma.Activities.Guardrails.csproj`
(references `Microsoft.CodeAnalysis.CSharp.Workspaces` at the repo-pinned Roslyn version;
`<EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>`),
`GuardrailDiagnostics.cs` (`TAMMA001`, title, `category="Tamma.Architecture"`,
`DiagnosticSeverity.Error`, `helpLinkUri` → design doc), `Allowlist.cs`
(`IsEngineSurface`, `ApiClientType`, `CallbackHostConfigKey`, `VendorDenylist`, `ExemptBaseTypes`).

**Tests (first):** `tests/Tamma.Activities.Guardrails.Tests/GuardrailDiagnosticsTests.cs` — id is
`TAMMA001`; category `Tamma.Architecture`; `DefaultSeverity == Error`; `IsEnabledByDefault`.

**Acceptance:**
- [ ] Analyzer project builds as `netstandard2.0`; descriptor severity is `Error`.
- [ ] Allow/deny/exempt sets encode the §1.2 / §5.3 data.

### T2 — Detection pass (a): direct external HTTP call (AC2/AC4)

**Scope:** `RegisterOperationAction(OperationKind.Invocation)` (gated by `RegisterCompilationStartAction`
+ `IsEngineSurface`). Flag `HttpClient.PostAsync`/`PostAsJsonAsync`/`PutAsJsonAsync`/`SendAsync`/
`GetAsync`-family where the receiver is not a `TammaApiClient`-owned client and the URL arg host is not
`Engine:CallbackUrl`-rooted. Allow `IHttpClientFactory` usage feeding those two.

**Files:** `EngineExternalCallAnalyzer.cs` (invocation pass).

**Tests (first):** `tests/Tamma.Activities.Guardrails.Tests/EngineExternalCallAnalyzerTests.cs` +
`Fixtures/`:
- positive: `httpClient.PostAsJsonAsync("https://api.github.com/...")` in a `Tamma.Activities`-named
  compilation → one `TAMMA001`.
- negative: `_api.QueueSlackNotificationAsync(...)` / `_api.CallLlmAsync(...)` (TammaApiClient) → none.
- negative: `PostAsJsonAsync($"{callbackUrl}/api/engine/...")` (Engine:CallbackUrl) → none.

**Acceptance:**
- [ ] Direct vendor HTTP call → `TAMMA001` Error.
- [ ] `TammaApiClient` + engine-callback calls → no diagnostic.

### T3 — Detection pass (b): credential-holding vendor-service injection (AC3)

**Scope:** `RegisterSymbolAction(SymbolKind.Method)` for constructors + `SymbolKind.Field` for injected
fields. Flag a parameter/field whose type ∈ `VendorDenylist`
(`Octokit.IGitHubClient`, `IGitHubActionsClient`, `IIntegrationService`, `IProviderCredentialResolver`,
future Slack/Stripe clients).

**Files:** `EngineExternalCallAnalyzer.cs` (injection pass).

**Tests (first):** extend `EngineExternalCallAnalyzerTests` — ctor taking `Octokit.IGitHubClient` /
`IGitHubActionsClient` / `IProviderCredentialResolver` / a Slack client → `TAMMA001`; a ctor taking
`TammaApiClient` / `ILogger` / `IHttpClientFactory` → none.

**Acceptance:**
- [ ] Denylisted vendor-service injection → `TAMMA001` Error.
- [ ] Sanctioned dependencies → no diagnostic.

### T4 — Exemptions + non-engine-surface skip (AC5/AC6)

**Scope:** Ensure the §5.3 exempt categories never trip, and that the analyzer does nothing on a
non-engine-surface compilation (e.g. `Tamma.Api`).

**Files:** `EngineExternalCallAnalyzer.cs` (exempt check on the containing type's base/interface set);
`Allowlist.ExemptBaseTypes`.

**Tests (first):** extend the analyzer tests —
- `ICLIAgentProvider` impl with a local-process call → none.
- local tool (`FileReadTool`/`ShellExecuteTool`/`GitOperationsTool`) → none.
- `WebhookSignalRegistry` (inbound) → none.
- the *same* direct vendor call inside a `Tamma.Api`-named compilation → none (API is allowed).

**Acceptance:**
- [ ] Exempt categories never flagged; non-engine-surface never flagged.

### T5 — Project-reference wiring + solution (AC1/AC8)

**Scope:** Reference the analyzer from `Tamma.Activities` and `Tamma.ElsaServer` as an `Analyzer`; add
the analyzer + its test project to `Tamma.sln`. Confirm a violation fails `dotnet build Tamma.sln -c Release`.

**Files:** modify `src/Tamma.Activities/Tamma.Activities.csproj` +
`src/Tamma.ElsaServer/Tamma.ElsaServer.csproj`
(`<ProjectReference ... OutputItemType="Analyzer" ReferenceOutputAssembly="false" />`); modify
`Tamma.sln`.

**Tests (first):** a CI-shaped smoke — `dotnet build Tamma.sln -c Release` is clean on the real (post-cutover)
engine surface; a temporary bad fixture compiled into `Tamma.Activities` would fail it (proven via the
analyzer test, not by polluting the real tree).

**Acceptance:**
- [ ] Analyzer runs during `dotnet build Tamma.sln`; clean `main` passes.
- [ ] A re-introduced violation fails the build (no new CI job).

### T6 — Reflection backstop + suppression-resistance (AC7/AC8)

**Scope:** Defense-in-depth: a reflection test on the built `Tamma.Activities` assembly + a
positive-control proving the analyzer is wired + a check that no `TAMMA001` suppression exists under the
engine surface.

**Files:** new `tests/Tamma.Activities.Tests/Guardrails/ActivitiesGuardrailTests.cs`;
`Fixtures/` known-bad assembly for the positive control.

**Tests (first):**
- the real `Tamma.Activities` assembly has no ctor/field of a denylisted vendor type (post-cutover clean).
- a positive-control fixture type trips `TAMMA001` (analyzer active, not suppressed).
- no `#pragma warning disable TAMMA001` / `<NoWarn>TAMMA001</NoWarn>` exists under the engine surface
  (grep + assertion).

**Acceptance:**
- [ ] Reflection backstop green on the clean assembly; positive control trips.
- [ ] No suppression of `TAMMA001` anywhere under the engine surface.

---

## Story order & dependencies

**Sequence 38-4 LAST in Epic 38** — after 32-5 (removes the nine direct-LLM callers) and 38-1/38-2/38-3
(git/agent-dispatch/Slack cutovers), so the engine surface is already clean and the analyzer passes from
day one. The allowlist references `TammaApiClient` + the client methods those stories add
(`CallLlmAsync`, the git/agent-dispatch methods, `QueueSlackNotificationAsync`). Forward-compatible with
**Epic 35** (add the Stripe SDK type to the denylist when referenced). No runtime consumers — every
future Epic-32/38 story is implicitly protected.

## Verification

```bash
# build is the PRIMARY check — the analyzer-as-Error fails it on a violation
dotnet build apps/tamma-elsa/Tamma.sln --no-restore -c Release
# analyzer unit tests + reflection backstop
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Guardrails.Tests/"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~Guardrails"
# suppression-resistance: no one turned the gate off
grep -rn "TAMMA001" apps/tamma-elsa/src/Tamma.Activities apps/tamma-elsa/src/Tamma.ElsaServer | grep -i "disable\|NoWarn"   # expect: none
# the rule it backstops, also as a raw grep (the analyzer is the real gate; this is a sanity check)
grep -rn "api.anthropic.com\|api.openai.com\|api.github.com\|chat.postMessage\|api.stripe.com" apps/tamma-elsa/src/Tamma.Activities   # expect: none
```

## Risks

- **`Warning` severity → does not fail the build** (`TreatWarningsAsErrors=false` repo-wide).
  Mitigation: `DefaultSeverity = Error`; descriptor-severity test; CI `dotnet build` proves failure.
- **False positive on a sanctioned seam → contributors disable the analyzer.** Mitigation: precise
  allowlist (`TammaApiClient` + `Engine:CallbackUrl` + `IHttpClientFactory` feeding them) + explicit
  §5.3 exemptions; negative-control + exemption tests; help message points to the fix.
- **Suppression bypass (`#pragma`/`<NoWarn>`).** Mitigation: T6 reflection/grep assertion that no
  `TAMMA001` suppression exists under the engine surface; positive-control fixture proves it is active.
- **Dynamic `HttpClient` path evades the syntactic analyzer.** Mitigation: the reflection backstop +
  the *injection* denylist (the usual entry point for a vendor client is being injected).
- **Roslyn analyzer-testing API drift.** Mitigation: WebSearch the latest
  `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` API before coding; pin to the repo Roslyn version;
  fixtures-first TDD surfaces breaks early. (Per global instruction: research latest docs before using
  analyzer/CLI APIs — never assume.)
- **Lands before the cutover → fails the build on pre-existing violations.** Mitigation: sequence last;
  if it fails on `main`, it is catching a real un-migrated violation — fix the violation, not the analyzer.
- **Epic 35 Stripe (by design).** Adding the Stripe SDK type to the denylist makes a `BillingActivity`
  injecting it fail the build automatically — the §1.2 "enforce by design" guarantee; no analyzer rewrite.
