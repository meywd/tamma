# Story 38-4: Build-Time Guardrail Analyzer (rule-1 enforcement)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform maintainer responsible for the permanence of the "steps never call external APIs directly" rule**,
I want a build-failing guardrail — a Roslyn analyzer plus a reflection-backed architecture test — that **fails the build** if any class under `Tamma.Activities` references `HttpClient`/`PostAsync`/`PostAsJsonAsync`/`SendAsync` to a non-`TammaApiClient` host, or injects a credential-holding vendor service (Octokit, a Slack/Stripe client, `IIntegrationService` vendor members, a raw `IProviderCredentialResolver`),
So that **the rule-1 invariant cannot silently regress** after Epics 32 and 38 have done the one-time cutover work — a re-introduced direct external call (or a co-hosted vendor-service injection) can never compile, so no future story, refactor, or merge can put a live external credential back into the engine process.

## Priority

P0 (permanence) — This is the **permanent backstop** for the entire "steps never call external APIs" rule across Epics 32 and 38. The cutover stories (32-5 for LLM, 38-1 git, 38-2 agent-dispatch, 38-3 Slack) each remove the *current* violations once; **38-4 stops them from ever coming back.** Without it, the invariant is enforced only by reviewer vigilance and grep — both of which fail silently the moment someone adds a convenient `httpClient.PostAsJsonAsync(vendorUrl)` "just for this one activity." The design mandates it explicitly (§5.2): *"Add a guardrail analyzer/test: fail the build if any class under `Tamma.Activities` references `HttpClient`/`PostAsync`/`PostAsJsonAsync` to a non-`TammaApiClient` host, or injects a credential-holding vendor service — so violations can't reappear."* It is cheap to add and pays compounding returns forever.

## Context

### What the rule is (and why grep is not enough)

Rule 1 (design §0/§1): **a workflow STEP MUST NEVER call an external API/provider directly.** A step that needs an external effect delegates over HTTP to a `Tamma.Api` endpoint through **`TammaApiClient`**; the credential-holding code, the authorization decision, the external HTTP call, and the metering/audit emission all live in `Tamma.Api`. The engine (`Tamma.ElsaServer` / `Tamma.Activities`) holds **no** external credential and hits **no** external endpoint.

The cutover stories enforce this once: 32-5 routes the nine in-engine direct-LLM callers through `/api/v1/llm/call`; 38-1/38-2/38-3 route the git/agent-dispatch/Slack `VIOLATION-by-co-hosting` activities through their endpoints. After they land, the audit table (design §1.2) should show **zero** in-engine credential holders. But nothing prevents a future PR from re-adding one — the violation compiles fine, passes tests in the co-hosted single-process deploy (where the injected vendor service happens to resolve), and only fails catastrophically the moment the engine runs as per-tenant dedicated compute (Cranl), where the token would have to be pushed into the engine process. **Grep in CI is brittle** (string-only, no semantic model, easily evaded by an alias or a helper indirection). The correct enforcement is a **compile-time** check with the Roslyn semantic model.

### What this story builds

Two complementary mechanisms (belt-and-suspenders — see AC-level rationale):

1. **A Roslyn `DiagnosticAnalyzer`** (`Tamma.Activities.Guardrails`, a `netstandard2.0` analyzer project) referenced by `Tamma.Activities` (and the other engine projects) as an `Analyzer`. It walks the semantic model of every type whose containing project is the engine surface and **reports a build error** (diagnostic id **`TAMMA001`**) when it finds:
   - a member access / invocation of `HttpClient.PostAsync` / `PostAsJsonAsync` / `GetAsync`-family / `SendAsync` whose target host is **not** `TammaApiClient` and not the engine-callback host (`Engine:CallbackUrl`-rooted internal endpoints, as `TriggerCIActivity` uses); or
   - a constructor parameter / injected field whose type is a **credential-holding vendor service** on the denylist (`Octokit.*`, a Slack/Stripe SDK client, the Slack/GitHub vendor members of `IIntegrationService`, `IProviderCredentialResolver`, `IGitHubActionsClient`, …).
   `TreatWarningsAsErrors` is `false` repo-wide, so the diagnostic is declared at **`DiagnosticSeverity.Error`** to fail the build regardless.

2. **A reflection-backed architecture test** (`ActivitiesGuardrailTests`, in the existing test suite) that loads the `Tamma.Activities` assembly and asserts the same allowlist at test time — a defense-in-depth net that catches anything the analyzer's syntactic patterns miss (e.g. an `HttpClient` obtained via an unusual factory path, or a vendor type referenced only through a transitively-injected field). It also asserts the analyzer itself is wired (a known-bad fixture type triggers `TAMMA001`).

### The allowlist (the only sanctioned outbound seams)

A type under the engine surface may reach "outside the process" **only** through:

- **`TammaApiClient`** — the engine→API delegation seam (Bearer `Tamma:ApiToken` + `X-Tenant-Id`). All mediated effects (LLM via 32-5, git via 38-1, agent-dispatch via 38-2, Slack via 38-3) go through it.
- **The engine-callback host** — internal endpoints rooted at `Engine:CallbackUrl` (the pattern `TriggerCIActivity` already uses: `PostAsJsonAsync($"{callbackUrl}/api/engine/...")`). These terminate inside Tamma's own API plane, not at an external vendor.
- **`IHttpClientFactory` is permitted ONLY when the resulting client is used by `TammaApiClient` or to call an `Engine:CallbackUrl` host.** A raw `_httpClientFactory.CreateClient()` followed by a `PostAsJsonAsync` to a vendor URL is a violation. (The analyzer resolves the call target host where statically determinable; the reflection test backstops the dynamic cases.)

Everything else — `api.anthropic.com`, `api.openai.com`, `api.github.com`/Octokit, `slack.com`, `api.stripe.com`, or any injected vendor-credential service — is **denied**.

### Explicitly out of scope

- **Fixing the current violations** — that is the cutover work of 32-5 / 38-1 / 38-2 / 38-3. This story assumes those have landed (or codes the allowlist so that, until they land, the *known-and-tracked* in-flight violations are the only failures). The analyzer is the **backstop**, not the migrator.
- **Enforcing the rule on `Tamma.Api`** — the API is *supposed* to hold credentials and call vendors; the analyzer targets the **engine surface only** (`Tamma.Activities` + `Tamma.ElsaServer`), never `Tamma.Api`.
- **`ICLIAgentProvider` local CLI agents, in-engine local tools (`FileReadTool`/`ShellExecuteTool`/`GitOperationsTool`), and inbound webhook receivers** — these are legitimately exempt (design §5.3): a local process / local filesystem / inbound signal is **not** an external API call. The allowlist must NOT flag them.

## Acceptance Criteria

1. **A Roslyn analyzer project exists and is referenced by the engine surface.** A new `apps/tamma-elsa/src/Tamma.Activities.Guardrails/` analyzer project (`netstandard2.0`, references `Microsoft.CodeAnalysis.CSharp.Workspaces` at the repo-pinned Roslyn version) is added to `Tamma.sln` and referenced by `Tamma.Activities` (and `Tamma.ElsaServer`) via an `<Analyzer>` / `OutputItemType="Analyzer"` `ProjectReference`. It runs during `dotnet build Tamma.sln`.

2. **`TAMMA001` fails the build on a direct external HTTP call from the engine surface.** When a type whose containing assembly is `Tamma.Activities` or `Tamma.ElsaServer` invokes `HttpClient.PostAsync`/`PostAsJsonAsync`/`PutAsJsonAsync`/`SendAsync`/`GetAsync`(-family) where the statically-resolvable target host is **not** `TammaApiClient` and **not** an `Engine:CallbackUrl`-rooted internal endpoint, the analyzer reports **`TAMMA001`** at **`DiagnosticSeverity.Error`**, failing the build (independent of `TreatWarningsAsErrors`, which is `false` repo-wide).

3. **`TAMMA001` fails the build on a credential-holding vendor-service injection.** When a type under the engine surface has a **constructor parameter or DI-injected field** whose type is on the vendor denylist — `Octokit.*` clients, a Slack SDK client, a Stripe SDK client, `IGitHubActionsClient`, the Slack/GitHub vendor members of `IIntegrationService`, and a raw `IProviderCredentialResolver` (the engine must not resolve credentials post-32-5) — the analyzer reports `TAMMA001` (Error) with a message naming the offending type and pointing to the mediation pattern.

4. **The allowlist is precise — no false positives on the sanctioned seams.** `TammaApiClient` itself (which legitimately owns the engine→API `HttpClient`), `TriggerCIActivity`'s `Engine:CallbackUrl` POST, and any thin-client activity that calls only `TammaApiClient.*` (the cutover outputs of 32-5/38-1/38-2/38-3) MUST NOT be flagged. The analyzer's allowlist explicitly exempts: the `TammaApiClient` type, calls to `Engine:CallbackUrl`-rooted hosts, and `IHttpClientFactory` usage that feeds those two.

5. **The exempt categories (design §5.3) are NOT flagged.** Local CLI agent providers (`ICLIAgentProvider` implementations), in-engine local tools (`FileReadTool`/`ShellExecuteTool`/`GitOperationsTool` — local process/filesystem, not external HTTP), and inbound webhook receivers (`WebhookSignalRegistry` — inbound, no outbound call) MUST NOT trigger `TAMMA001`. The analyzer targets **outbound external HTTP + vendor-credential injection**, nothing else.

6. **A clear diagnostic id, category, and message.** Id **`TAMMA001`**, title "Engine step makes a direct external call or injects a vendor credential", category **`Tamma.Architecture`**, default severity **Error**, with a message of the form: ``"`{TypeName}` performs a direct external call / injects credential-holding `{VendorType}`. Engine steps must delegate to `Tamma.Api` via `TammaApiClient` (rule 1; design §1). See the `/api/v1/llm/call` (32-5) / `/api/v1/git/*` (38-1) / `/api/v1/notifications/slack` (38-3) mediation pattern."`` plus a `helpLinkUri` to the design doc. The id is documented (a `## TAMMA001` reference) so a developer who hits it knows the fix.

7. **A reflection-backed architecture test backstops the analyzer (defense in depth).** A new `ActivitiesGuardrailTests` (in the existing C# test suite) loads the `Tamma.Activities` assembly via reflection and asserts: no public/internal type's constructors take a denylisted vendor-credential type; no type holds a non-`TammaApiClient` `HttpClient` field used for a vendor call (to the extent statically assertable); and a **positive control** — a deliberately-bad fixture type compiled into a test asset triggers `TAMMA001` (proving the analyzer is actually wired, not silently disabled). The test runs in `dotnet test Tamma.sln`.

8. **Wired into the CI gate.** Because the analyzer is referenced by `Tamma.Activities`/`Tamma.ElsaServer`, the existing CI step `dotnet build Tamma.sln --no-restore -c Release` (`.github/workflows/ci.yml`, `working-directory: apps/tamma-elsa`) **already fails** on a `TAMMA001` Error — no new CI job is required, but the story documents that the build step is the gate and that the analyzer must not be suppressed (no blanket `<NoWarn>TAMMA001</NoWarn>` / `#pragma warning disable TAMMA001` is permitted; a per-line suppression requires a justification comment and is itself flagged by the reflection test if applied under the engine surface).

9. **Mode-independent (no per-mode scoping).** This is a **build-time** static guardrail. It does not read tenant state, does not run at request time, and has **no single-user vs SaaS scoping** — it applies identically to every build of the engine surface regardless of deployment mode. The story's per-mode section states this explicitly (the rule it enforces is *why* the modes stay safe, but the analyzer itself is mode-independent).

10. **No new control-plane table, no runtime entity.** This story adds **no** database table, no EF migration, no CP entity → **no** `Program.cs` DROP-list entry and **no** `ControlPlaneDbContextModelTests` change. It is pure build tooling (an analyzer project + a test + project-reference wiring).

11. **Tests cover positive controls, negative controls, and exemptions.** The analyzer's own unit tests (using `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing`) assert: a direct `PostAsJsonAsync(vendorUrl)` → `TAMMA001`; an Octokit/`IGitHubActionsClient`/`IProviderCredentialResolver`/Slack-client injection → `TAMMA001`; a `TammaApiClient.*` call → **no diagnostic**; a `TriggerCIActivity`-style `Engine:CallbackUrl` POST → **no diagnostic**; an `ICLIAgentProvider`/local-tool/inbound-webhook type → **no diagnostic**. The reflection test asserts the real `Tamma.Activities` assembly is clean (post-cutover) and the positive-control fixture fails.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Activities.Guardrails/        # NEW analyzer project (netstandard2.0)
  Tamma.Activities.Guardrails.csproj                    # references Microsoft.CodeAnalysis.CSharp.Workspaces (repo-pinned)
  EngineExternalCallAnalyzer.cs                         # the DiagnosticAnalyzer — reports TAMMA001
  GuardrailDiagnostics.cs                               # DiagnosticDescriptor for TAMMA001 (id/title/category/severity/helpLink)
  Allowlist.cs                                          # allowed seams (TammaApiClient, Engine:CallbackUrl) + vendor denylist

apps/tamma-elsa/src/Tamma.Activities/
  Tamma.Activities.csproj                               # MODIFY — <ProjectReference OutputItemType="Analyzer" ReferenceOutputAssembly="false" .../>
apps/tamma-elsa/src/Tamma.ElsaServer/
  Tamma.ElsaServer.csproj                               # MODIFY — same analyzer reference

apps/tamma-elsa/Tamma.sln                               # MODIFY — add the analyzer project

apps/tamma-elsa/tests/Tamma.Activities.Tests/Guardrails/
  ActivitiesGuardrailTests.cs                           # NEW — reflection backstop + positive-control assertion
apps/tamma-elsa/tests/Tamma.Activities.Guardrails.Tests/   # NEW — analyzer unit tests
  EngineExternalCallAnalyzerTests.cs                    # NEW — Roslyn analyzer test cases (positive + negative + exempt)
  Fixtures/                                             # NEW — known-bad + known-good source fixtures
```

### The diagnostic (`GuardrailDiagnostics.cs`)

```csharp
public static class GuardrailDiagnostics
{
    public const string Id = "TAMMA001";

    public static readonly DiagnosticDescriptor EngineDirectExternalCall = new(
        id: Id,
        title: "Engine step makes a direct external call or injects a vendor credential",
        messageFormat:
            "'{0}' performs a direct external call / injects credential-holding '{1}'. " +
            "Engine steps must delegate to Tamma.Api via TammaApiClient (rule 1; design §1). " +
            "See the /api/v1/llm/call (32-5) / /api/v1/git/* (38-1) / /api/v1/notifications/slack (38-3) pattern.",
        category: "Tamma.Architecture",
        defaultSeverity: DiagnosticSeverity.Error,     // Error so it fails the build (TreatWarningsAsErrors is false repo-wide)
        isEnabledByDefault: true,
        description: "A workflow step must never call an external API/provider directly or hold an external credential.",
        helpLinkUri: "https://github.com/meywd/tamma/blob/main/docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md");
}
```

### The analyzer (`EngineExternalCallAnalyzer.cs`) — detection strategy

```csharp
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EngineExternalCallAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(GuardrailDiagnostics.EngineDirectExternalCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        // Only the ENGINE surface — skip Tamma.Api (it is SUPPOSED to call vendors).
        context.RegisterCompilationStartAction(start =>
        {
            if (!Allowlist.IsEngineSurface(start.Compilation.AssemblyName)) return;   // Tamma.Activities / Tamma.ElsaServer only

            // (a) outbound HTTP calls to a non-allowlisted host
            start.RegisterOperationAction(ctx => InspectInvocation(ctx), OperationKind.Invocation);
            // (b) credential-holding vendor-service injection (ctor params + injected fields)
            start.RegisterSymbolAction(ctx => InspectConstructor(ctx), SymbolKind.Method);   // ctors
            start.RegisterSymbolAction(ctx => InspectField(ctx), SymbolKind.Field);
        });
    }
    // InspectInvocation: target is HttpClient.PostAsync/PostAsJsonAsync/SendAsync/GetAsync-family
    //   AND the receiver is not a TammaApiClient-owned client AND the URL arg host is not Engine:CallbackUrl
    //   -> report TAMMA001 (Error). Exempt ICLIAgentProvider / local tools / inbound webhook types.
    // InspectConstructor/InspectField: parameter/field type ∈ Allowlist.VendorDenylist -> report TAMMA001.
}
```

**Detection strategy — analyzer AND reflection test (both, deliberately):**

| Layer | Catches | Why both |
|---|---|---|
| **Roslyn analyzer (primary)** | Statically-resolvable direct vendor HTTP calls + denylisted-type ctor/field injections, at **compile time** — the violation never builds. | Compile-time is the strongest gate; uses the semantic model (not strings), so an alias or `using` rename can't evade it. |
| **Reflection architecture test (backstop)** | Denylisted vendor types reachable via reflection on the built `Tamma.Activities` assembly, plus the **positive control** that proves the analyzer is wired (not suppressed). | Catches dynamically-obtained `HttpClient` paths the syntactic analyzer can't statically resolve, and detects if someone disables/suppresses `TAMMA001`. |

### The allowlist (`Allowlist.cs`)

```csharp
internal static class Allowlist
{
    // The ONLY sanctioned outbound seams from the engine surface.
    public static bool IsEngineSurface(string? assemblyName) =>
        assemblyName is "Tamma.Activities" or "Tamma.ElsaServer";

    public const string ApiClientType = "Tamma.Activities.LlmCall.TammaApiClient";   // the engine→API seam
    public const string CallbackHostConfigKey = "Engine:CallbackUrl";                 // internal-endpoint host

    // Credential-holding vendor services the engine may NEVER inject (design §1.2):
    public static readonly ImmutableHashSet<string> VendorDenylist = ImmutableHashSet.Create(
        "Octokit.IGitHubClient", "Octokit.GitHubClient",
        "Tamma.Core.Interfaces.IGitHubActionsClient",
        "Tamma.Core.Interfaces.IIntegrationService",          // its Slack/GitHub vendor members hold tokens
        "Tamma.Api.Services.Providers.IProviderCredentialResolver",  // engine must not resolve creds post-32-5
        /* Slack SDK client, Stripe SDK client when added */ );

    // Exempt (design §5.3) — NOT external API calls:
    public static readonly ImmutableHashSet<string> ExemptBaseTypes = ImmutableHashSet.Create(
        "Tamma.Providers.ICLIAgentProvider",                  // local process, single-user
        /* local tools: FileReadTool/ShellExecuteTool/GitOperationsTool — local fs/process */
        "Tamma.Activities.AgentDispatch.WebhookSignalRegistry" /* inbound */ );
}
```

### Project-reference wiring (the gate)

```xml
<!-- Tamma.Activities.csproj / Tamma.ElsaServer.csproj -->
<ItemGroup>
  <ProjectReference Include="..\Tamma.Activities.Guardrails\Tamma.Activities.Guardrails.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Because the analyzer is referenced this way, the existing CI step `dotnet build Tamma.sln --no-restore -c Release` (`.github/workflows/ci.yml`) is the gate — a `TAMMA001` Error fails the build with no new job. The analyzer is declared at `DiagnosticSeverity.Error` precisely because `Directory.Build.props` sets `TreatWarningsAsErrors=false`, so a `Warning`-severity diagnostic would not fail the build.

## Dependencies

**Internal (sequencing — the violations this protects):**

- **32-5** (`/api/v1/llm/call`) — removes the nine in-engine direct-LLM callers. 38-4's allowlist assumes those are gone; **land 38-4 after 32-5** so the engine surface is already clean (else the analyzer fails on the pre-cutover violations — which is *correct* but blocks the build for the wrong story).
- **38-1** (git platform mediation) / **38-2** (agent-dispatch mediation) / **38-3** (Slack/notifications mediation) — the sibling Class-A/C/D cutovers. 38-4 is the **last** story in Epic 38: it locks the door after 38-1/38-2/38-3 have moved everything through `TammaApiClient`. Its allowlist references `TammaApiClient` and the new `QueueSlackNotificationAsync`/git/agent-dispatch client methods those stories add.
- **Epic 35** (billing) — 38-4's vendor denylist is **forward-compatible**: when Epic 35 adds a Stripe client, a `BillingActivity` injecting it (instead of emitting a `/api/v1/billing/*` intent or a `billing_outbox` row per 38-3's enforce-by-design subsection) **fails the build** under `TAMMA001`. Add the Stripe SDK type to the denylist when the SDK is referenced.

**Reference (the sanctioned seams the allowlist encodes):**

- `TammaApiClient` (`Tamma.Activities/LlmCall/TammaApiClient.cs`) — the allowed engine→API seam.
- `TriggerCIActivity` (`Tamma.Activities/Testing/TriggerCIActivity.cs`) — the allowed `Engine:CallbackUrl` internal-endpoint pattern.
- `QueueWelcomeEmailActivity` (`Tamma.Activities/TenantLifecycle/QueueWelcomeEmailActivity.cs`) — the outbox pattern (no external call from the engine; the out-of-band sender in the API holds the credential).

**External:** `Microsoft.CodeAnalysis.CSharp.Workspaces` + `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` (test) at the repo-pinned Roslyn version. No runtime external dependency.

**Consumers:** none — this is build tooling. Every future Epic-32/38 story is implicitly "protected" by it.

## Testing Strategy

1. **Positive control — direct vendor HTTP call.** A fixture type under a `Tamma.Activities`-named compilation that does `httpClient.PostAsJsonAsync("https://api.github.com/...")` → analyzer reports exactly one `TAMMA001` at `Error`.
2. **Positive control — vendor-service injection.** A fixture ctor taking `Octokit.IGitHubClient` / `IGitHubActionsClient` / `IProviderCredentialResolver` / a Slack client → `TAMMA001`.
3. **Negative control — `TammaApiClient` call.** A thin-client activity calling only `TammaApiClient.QueueSlackNotificationAsync(...)` / `CallLlmAsync(...)` → **no** diagnostic.
4. **Negative control — engine callback.** A `TriggerCIActivity`-shaped `PostAsJsonAsync($"{callbackUrl}/api/engine/...")` where `callbackUrl` is `Engine:CallbackUrl` → **no** diagnostic.
5. **Exemptions (design §5.3).** An `ICLIAgentProvider` impl, a local tool (`FileReadTool`/`ShellExecuteTool`/`GitOperationsTool`), and `WebhookSignalRegistry` → **no** diagnostic.
6. **Non-engine surface.** The same direct call inside a `Tamma.Api`-named compilation → **no** diagnostic (the API is allowed to call vendors).
7. **Severity / build-failure.** Assert the descriptor's `DefaultSeverity == Error` and that a compilation with a violation reports a build-failing error even with `TreatWarningsAsErrors=false`.
8. **Reflection backstop (AC7).** `ActivitiesGuardrailTests` loads the real built `Tamma.Activities` assembly: no ctor/field of any type is a denylisted vendor type (asserts the post-cutover assembly is clean); and a positive-control fixture assembly triggers `TAMMA001` (proves the analyzer is wired, not suppressed).
9. **Suppression-resistance (AC8).** A test asserts no `#pragma warning disable TAMMA001` / `<NoWarn>` for `TAMMA001` exists under the engine surface (grep + reflection assertion), so the gate can't be quietly turned off.
10. **Help/id stability.** Assert the diagnostic id is `TAMMA001`, category `Tamma.Architecture`, and the message names the offending type and the mediation pattern (so a developer who hits it can self-serve the fix).

Analyzer unit tests use `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing`. Docker-bound suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper — and `dotnet build` is itself the primary check here).

## Estimated Effort

3-4 days (one analyzer with two detection passes + the allowlist/denylist + analyzer unit tests with fixtures + the reflection backstop + the project-reference wiring across two engine projects + the diagnostic documentation). The Roslyn analyzer-testing harness setup is the main cost; the rules themselves are small.

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Tamma.Activities.Guardrails.csproj` | Create (netstandard2.0 analyzer project) |
| `apps/tamma-elsa/src/Tamma.Activities.Guardrails/GuardrailDiagnostics.cs` | Create (`TAMMA001` descriptor) |
| `apps/tamma-elsa/src/Tamma.Activities.Guardrails/EngineExternalCallAnalyzer.cs` | Create (the analyzer) |
| `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs` | Create (allowed seams + vendor denylist + exemptions) |
| `apps/tamma-elsa/src/Tamma.Activities/Tamma.Activities.csproj` | Modify (analyzer `ProjectReference`) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Tamma.ElsaServer.csproj` | Modify (analyzer `ProjectReference`) |
| `apps/tamma-elsa/Tamma.sln` | Modify (add analyzer project + its test project) |
| `apps/tamma-elsa/tests/Tamma.Activities.Guardrails.Tests/EngineExternalCallAnalyzerTests.cs` | Create (analyzer unit tests) |
| `apps/tamma-elsa/tests/Tamma.Activities.Guardrails.Tests/Fixtures/` | Create (known-bad + known-good source fixtures) |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Guardrails/ActivitiesGuardrailTests.cs` | Create (reflection backstop + positive control) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions.
3. Read the design of record §1 (steps never call providers), §1.2 (the audit table the denylist is derived from), §5.2 (the explicit guardrail mandate), and §5.3 (the exempt categories the allowlist must NOT flag) IN FULL.
4. Confirmed 32-5 / 38-1 / 38-2 / 38-3 have landed so the engine surface is already clean (the analyzer should pass on `main`; if it fails, it is catching a real un-migrated violation — fix the violation, not the analyzer).
5. Reviewed `TammaApiClient`, `TriggerCIActivity`, and `QueueWelcomeEmailActivity` (the three sanctioned-seam exemplars the allowlist encodes) plus the WebSearch'd latest Roslyn `DiagnosticAnalyzer` + `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` API (never assume the analyzer-testing API shape — verify against the repo-pinned Roslyn version).
6. Planned the TDD approach — write the analyzer test fixtures (positive, negative, exempt) first; the analyzer is correct when all three classes of fixture assert as specified.

### Key Design Decisions

- **Analyzer (compile-time) AND reflection test (defense-in-depth).** The analyzer is the strong gate — the violation never compiles. The reflection test backstops dynamic `HttpClient` paths the syntactic analyzer can't statically resolve, and — crucially — proves the analyzer is **wired and not suppressed** via a positive-control fixture. Either alone is weaker.
- **`DiagnosticSeverity.Error`, not `Warning`.** `Directory.Build.props` sets `TreatWarningsAsErrors=false` repo-wide, so a `Warning` would not fail the build. The descriptor is declared at `Error` so the existing `dotnet build Tamma.sln` CI step is the gate with no new job.
- **Engine surface only.** The analyzer's `CompilationStartAction` early-returns unless the assembly is `Tamma.Activities` / `Tamma.ElsaServer`. `Tamma.Api` is *supposed* to hold credentials and call vendors — flagging it would be wrong.
- **Allowlist by sanctioned seam, denylist by vendor type.** Outbound HTTP is allowed only to `TammaApiClient`-owned clients and `Engine:CallbackUrl` hosts; injection is denied for the specific credential-holding vendor types in the §1.2 audit table. This pair maps directly onto the design's "right vs wrong" reference set.
- **The exempt categories are first-class.** `ICLIAgentProvider`, local tools, and inbound webhooks (design §5.3) are explicitly exempted so the guardrail does not punish the legitimately-local single-user path or inbound signals — a false positive there would push contributors to disable the analyzer, defeating its purpose.
- **Forward-compatible denylist.** The vendor denylist is data, not logic — Epic 35 adds the Stripe SDK type and a future `BillingActivity` that injects it fails the build, with no analyzer rewrite. This is the §1.2 "Enforce by design" row made real.
- **No runtime footprint (AC10).** Pure build tooling: no DB table, no migration, no CP entity → no `Program.cs` DROP-list entry and no `ControlPlaneDbContextModelTests` change.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

This guardrail is **mode-independent.** It is a build-time static analyzer plus a test; it does not read tenant or user state, does not run at request time, and has no principal. The single-user vs SaaS distinction does not apply to it.

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of the guardrail? | None — it is a compile-time/test-time check with no request principal. | None — same. |
| Does scoping (`TenantId`/`UserId`) apply? | No. The analyzer inspects source/IL, not tenant data. | No — identical. |
| What does it protect, per mode? | It enforces rule 1 so the engine holds no external credential — which is *why* the single-user local-harness path and the SaaS API-only path both stay safe. The enforcement is the same build for both. | Same build, same rule. The invariant it guards is mode-sensitive (SaaS dedicated compute is where a leaked engine token is most dangerous), but the **check** is not. |
| Mode source | N/A (build time — `ITammaModeProvider` is not consulted). | N/A. |

The exempt category `ICLIAgentProvider` is *single-user-relevant* (those providers are single-user-only per design §5.3), but the analyzer exempts the **type**, not the runtime mode — it never consults `ITammaModeProvider`.

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Analyzer declared `Warning` → does not fail the build (`TreatWarningsAsErrors=false`) | Critical | Declare `DefaultSeverity = Error`; a test asserts the descriptor severity; CI `dotnet build` proves a violation fails. |
| False positive on a sanctioned seam → contributors disable the analyzer | High | Precise allowlist (`TammaApiClient` + `Engine:CallbackUrl` + `IHttpClientFactory` feeding them) and explicit §5.3 exemptions; negative-control + exemption tests; help message points to the fix. |
| Someone suppresses `TAMMA001` (`#pragma`/`<NoWarn>`) to bypass the gate (AC8) | High | The reflection test asserts no `TAMMA001` suppression exists under the engine surface; the positive-control fixture proves the analyzer is active. |
| Dynamic `HttpClient` path evades the syntactic analyzer | Medium | The reflection architecture test backstops the cases the semantic model can't statically resolve; the denylist also blocks the *injection* of the vendor client, which is the usual entry point. |
| Roslyn analyzer-testing API drift across versions | Medium | WebSearch the latest `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` API before coding; pin to the repo's Roslyn version; fixtures-first TDD surfaces API breaks early. |
| Analyzer lands before the cutover → fails the build on pre-existing violations | Medium | Sequence 38-4 **last** in Epic 38 (after 32-5/38-1/38-2/38-3); on `main` the engine surface must already be clean, so the analyzer passes from day one. |
| Epic 35 adds Stripe and a billing step injects it | Low (by design) | The denylist is forward-compatible — add the Stripe SDK type when referenced; the violation then fails the build automatically (the §1.2 "enforce by design" guarantee). |

### Success Metrics

- [ ] `dotnet build Tamma.sln -c Release` **fails** when a fixture re-introduces a direct vendor call or a vendor-credential injection under the engine surface, and **passes** on the clean `main`.
- [ ] The analyzer flags **zero** false positives on `TammaApiClient`, `TriggerCIActivity`, the §5.3 exempt categories, and the thin-client outputs of 32-5/38-1/38-2/38-3.
- [ ] The reflection backstop asserts the real `Tamma.Activities` assembly is clean **and** the positive-control fixture trips `TAMMA001` (analyzer proven wired).
- [ ] No `TAMMA001` suppression exists anywhere under the engine surface.
- [ ] A future `BillingActivity` injecting a Stripe client would fail the build (verified by a denylist-extension test fixture).

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§0/§1 rule 1; §1.2 audit table → the vendor denylist; §5.2 the explicit guardrail-analyzer mandate; §5.3 the exempt categories)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-38-4-build-time-guardrail-analyzer-plan.md`
- Sibling stories (the violations this protects): `story-38-1/` (git platform mediation), `story-38-2/` (agent-dispatch mediation), `story-38-3/` (Slack/notifications mediation); `docs/stories/epic-32/story-32-5/` (the LLM mediation that removes the nine direct-LLM callers)
- Forward tie-in: **Epic 35** (billing) — the denylist extension that makes the Class-E "enforce by design" guarantee (design §1.2) real.
- Sanctioned-seam exemplars the allowlist encodes: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` (the allowed engine→API seam), `apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs` (allowed `Engine:CallbackUrl` host), `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/QueueWelcomeEmailActivity.cs` (the outbox pattern — no engine-side external call)
- Build wiring: `apps/tamma-elsa/Directory.Build.props` (`TreatWarningsAsErrors=false` → why `Error` severity), `.github/workflows/ci.yml` (`dotnet build Tamma.sln -c Release` — the gate)

## Logging Requirements

> This is a build-time analyzer, not a runtime service — it has no application logging. The "logging" here is the **build diagnostic output** the developer sees.

- **Build diagnostic (the only "log")**: `TAMMA001` Error at the offending source location, with the message naming the offending type and the mediation pattern to use (so the fix is self-serve). Emitted by the C# compiler / `dotnet build`, surfaced in CI build output.
- **Analyzer-test output**: standard xUnit assertions; the positive-control test names the fixture that should trip `TAMMA001`.
- **No structured runtime logging**: the analyzer reads source/IL only; it logs nothing at runtime.
- **Credential safety (LOAD-BEARING)**: the analyzer **must never read, embed, or emit any credential** — it operates purely on source/symbol shape. Diagnostic messages name **types** (`Octokit.IGitHubClient`, `IProviderCredentialResolver`), never values, URLs-with-tokens, or secrets. There is no path by which the guardrail itself could leak a key (it exists precisely to prevent keys from reaching the engine).

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — build-time guardrail analyzer (rule-1 enforcement). A Roslyn `DiagnosticAnalyzer` (`TAMMA001`, `Tamma.Architecture`, `DiagnosticSeverity.Error`) plus a reflection-backed `ActivitiesGuardrailTests` that **fail the build** if any class under the engine surface (`Tamma.Activities`/`Tamma.ElsaServer`) references `HttpClient`/`PostAsync`/`PostAsJsonAsync`/`SendAsync` to a non-`TammaApiClient`/non-`Engine:CallbackUrl` host, or injects a credential-holding vendor service (Octokit / `IGitHubActionsClient` / Slack/Stripe client / vendor `IIntegrationService` members / raw `IProviderCredentialResolver`). Allowlist (`TammaApiClient` + engine-callback hosts), denylist (the §1.2 audit-table vendor types), and explicit §5.3 exemptions (`ICLIAgentProvider`, local tools, inbound webhooks). Wired via an `OutputItemType="Analyzer"` `ProjectReference` so the existing `dotnet build Tamma.sln -c Release` CI step is the gate (no new job); `Error` severity chosen because `TreatWarningsAsErrors=false` repo-wide. Mode-independent (build-time, no per-mode scoping); no CP table/migration. The permanent backstop for the whole "steps never call external APIs" rule across Epics 32 + 38, forward-compatible with the Epic 35 Class-E (Stripe) enforce-by-design tie-in. Sequenced last in Epic 38, after 32-5/38-1/38-2/38-3. | Claude |
