# Implementation Plan — Story 42-4: Tool Credential / Secret Binding

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** 42-4's verdict is **"Unchanged"** — its own scope
survives intact. But **two of its four named dependencies were deleted**, so parts of the story text now
point at nothing. The deltas:

| Story file says | Reconciled |
|---|---|
| §1 / Dependencies: "42-2's binding may override the logical `Name` (`secret_binding_name`)" | **42-2 is DELETED.** There is no `tool_bindings` table and no per-principal secret-name override. The logical name comes from the descriptor's `SecretRequirement.Name` **only**. Epic 43's `action_assignments` stores policy (`MinAutonomy`, `Enforce`, `Enabled`, `AllowedRoles`) — **it has no `secret_binding_name` and no `ConfigJson`** — so nothing replaces it. Recorded as **G1** below, because it is a real capability gap, not a simplification. |
| §1: "A `Required` secret that does not resolve is a loud, typed 'capability unconfigured' failure at resolve time (**42-3 surfaces it**)" | **42-3 is DELETED.** There is no resolve-time pre-screen. The failure now surfaces at **invocation time**, inside `ExecuteAsync`, as `ToolExecutionResult { Success = false }` — which is the correct place anyway (§3's "fetched immediately before the external call"). D3 rewrites the mechanism; the guarantee (never an unauthenticated call) is unchanged. |
| AC6: "the tool is **not offered to the agent** and the step routes human-assigned" | **No mechanism exists to un-offer a tool.** That was 42-3 stage 1. Epic 43's Seam B is an invocation-time gate keyed on an `ActionKey`, not a resolve-time filter, and it does not consult secret availability. **AC6 is rewritten** (D6) to what is actually testable: a fail-closed `ToolExecutionResult`, one `TOOL.FAILED` row, no vendor call, no crash. The "route human-assigned" half is filed to Epic 43. |
| §4 / AC5: "appends a DCB `TOOL.SECRET_ACCESSED` event" | **Kept.** The reconciliation moved the *governance* events (`TOOL.RESOLVED`/`DENIED`/`ESCALATED`/`AUTHORIZED`) to Epic 43's single event family; `TOOL.SECRET_ACCESSED` is a secret-access record, not a governance decision, and is explicitly retained as 42-4's in the verdict for 42-5. |

**Unchanged and still in scope:** everything else — `IToolSecretProvider` in `Tamma.Api`, per-mode
`SecretRef` construction, never-hold + tag projection, the by-value-at-`ExecuteAsync`/pattern-after-it
redaction split, and the `ISecretAccessAuditor` no-op dependency.

## Scope & Deliverable

When this story is done, an external-touching `IToolExecutor` in `Tamma.Api` can call
`IToolSecretProvider.ResolveAsync(runTenantId, requirement, ct)` and receive a short-lived plaintext
credential plus a tag projection, with the `SecretRef` **constructed by the provider** from the run's tenant
identity — never supplied by the caller, the tool config, the tool arguments, or the model. Single-user
resolves `SecretRef.ForPlatform(name)`; SaaS resolves `SecretRef.ForTenant(runTenantId, name)`. The
credential is used for one external call and dropped; it never reaches `ToolExecutionResult.Output`, a DCB
event, a log line, or an error message. Every fetch emits one `TOOL.SECRET_ACCESSED` DCB row (ref storage
key + purpose + tenant tag, no value) and one `ISecretAccessAuditor` call. A `Required` secret that does not
resolve is a typed, fail-closed failure with zero vendor calls.

## Pre-Reading

- `docs/stories/epic-42/story-42-4/42-4-tool-credential-secret-binding.md` — the story (**read the Reconciled scope table first**)
- `docs/stories/epic-42/README.md` — "Credentials (Story 42-4)", decision **D1**, "Where the code lives", and the Out-of-scope note that this epic depends on a real `ISecretAccessAuditor` and does not build one
- `docs/stories/epic-42/story-42-1/implementation-plan.md` — D1 (the `SecretPurpose` relocation, which decides which namespace this story's `using` names), D2 (`ToolDescriptor(RequiredSecret, Suspends)`), D8 (siting)
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` — interface `:22`, **seven** methods `:29`, `:39`, `:46`, `:58`, `:70`, `:80`, `:89`; the no-plaintext contract `:10-16`
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretRef.cs:19-60` — properties `:21-23`, ctor `:25`, the three `ArgumentException` throws `:27-29` / `:33-36` / `:37-40`, factories `ForPlatform` `:49` / `ForTenant` `:53`, `ToStorageKey()` `:60`
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/SecretScope.cs:23-30` — exactly two members: `Platform` `:26`, `Tenant` `:29`
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretAccessAuditor.cs` — `SecretAuditEventTypes` `:9-37`, `SecretAuditOutcome` `:45`, `SecretAuditEvent` `:79-86`, the interface `:100` with its single member `EmitAsync` `:108`, and **`NullSecretAccessAuditor` `:118-123` — the only implementation in the solution**
- **`apps/tamma-elsa/src/Tamma.Api/Extensions/SecretsServiceCollectionExtensions.cs:52-56`** — where `NullSecretAccessAuditor` is actually registered (`TryAddSingleton`), under the comment *"Audit pipe — null until a future story wires the real one."* Reached from `Program.cs:504` via `AddTammaSecretCabinet`
- **`apps/tamma-elsa/src/Tamma.Api/Services/Platforms/SecretStorePlatformCredentialReader.cs`** — **the model for this story.** Class `:35`, ctor `:43-47`, `ReadActivePlaintextAsync(string scope, Guid? tenantId, string name, CancellationToken ct = default)` `:60-64`; the five audit emissions `:109` / `:116` / `:129` / `:134` / `:141` via `EmitReadAsync` `:148`; actor `Guid.Empty` `:157-158`. Registered conditionally `Program.cs:849-851`, else `NullPlatformCredentialReader` `:855-857`
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CabinetTenantProviderKeyReader.cs` — class `:26`, ctor `:32-35` (**no auditor**), `TryReadAsync` `:46-47`, the tenant-pinning EF predicate `:57-59`, degrade-to-null `:79` / `:83-91`
- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Stopgap/RuntimeSecretResolver.cs` — class `:25`, `DefaultCacheTtl = 60s` `:28`, cache `:37`, ctor `:41-48` (**no auditor**), `GetAsync` `:66`, `Invalidate` `:123`
- **`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Credentials/IProviderCredentialResolver.cs`** — the shape to generalize: `CredentialSource` `:8-14`, `ProviderCredential` `:33-37`, **`ToTag()` `:43-46`**, `ProviderCredentialTag` `:56-59`, the interface `:76` with `ResolveAsync` `:87-88` and `Invalidate` `:95`
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/DefaultProviderCredentialResolver.cs` — class `:37`, ctor `:54-63`, `ResolveAsync` `:84`, BYOK leg `:91-124`, platform leg `:127-146`, fail-closed `:148-172`, cache `:52` (**BYOK only**), `Invalidate` `:176-187`, direct event appends `:241` / `:270`
- `apps/tamma-elsa/src/Tamma.Data/Entities/SecretRow.cs:81-83` — `Purpose` is a `string` column, default `"generic"`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs` — `MaxOutputBytes = 50*1024` `:12`, `Truncate` `:23`, **`RedactSecrets` `:72-120` (≈10 regexes, pattern-based)**
- `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs:23` — `Clean(string?)` `:71`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ErrorRedactor.cs:30` — public API is exactly the ctor `:138` and `Redact(string)` `:144`; the 8-rule table `:121-132`; never throws `:173`
- `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17` (`IsEngineSurface`), **`:59`** (`IProviderCredentialResolver` on the injection denylist), `:57-58` (the note explaining why `InlineToolLoopRunner` needs no exemption)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:7` — `AppendAsync(DomainEvent evt)`, **no `CancellationToken`**

## Corrections to the story

- **Y1 — `NullSecretAccessAuditor` is registered in `SecretsServiceCollectionExtensions.cs:56`, not
  `Program.cs`.** The story quotes the comment correctly but gives no file. The registration is reached from
  `Program.cs:504` (`AddTammaSecretCabinet`) → `AddTammaSecrets()` (`:48`) → `TryAddSingleton` (`:56`). An
  implementer looking in `Program.cs` will not find it. The substantive claim is confirmed: **a repo-wide
  search for `: ISecretAccessAuditor` returns exactly one line — the null implementation.**
- **Y2 — `SecretStorePlatformCredentialReader` does NOT audit "every success and failure branch".** It audits
  the five branches that reach the store (`:109`, `:116`, `:129`, `:134`, `:141`) but **not** the four
  argument-validation throws (`:66`, `:67`, `:74`, `:79`, `:85`), which fire before the audit ref is built. A
  malformed call is therefore invisible to the audit trail. `IToolSecretProvider` must audit *its own*
  argument rejections (D5) rather than inheriting the gap — this matters because AC1(c)'s cross-tenant
  rejection is exactly an argument rejection.
- **Y3 — `ISecretStore` has seven methods, and the story's own count says "all seven return
  `SecretMetadata`/`SecretVersion`".** Verified true. The stronger claim in the interface's own doc
  (`:18-20`) — that the store "emits an `ISecretAccessAuditor` event for every read/write/rotate/retire/version
  probe" — is **structurally true and operationally false**, because the only auditor discards. The story's
  framing is right; the plan restates it so no AC is written against a persisted `SECRET.READ` row.
- **Y4 — three of the four runtime readers do not even accept an auditor.** `CabinetTenantProviderKeyReader`
  (ctor `:32-35`), `RuntimeSecretResolver` (ctor `:41-48`) and `DefaultAlertChannelSecretReader` inject no
  `ISecretAccessAuditor` at all. The story marks `RuntimeSecretResolver` "unaudited" but implies the other two
  are audited. Only `SecretStorePlatformCredentialReader` is — which is why D2 backs the provider on that one.
- **Y5 — `DefaultProviderCredentialResolver`'s event appends are unguarded.** `AlertEventEmitter` and
  `PromptEventsService` swallow-and-warn, `EscalationDispositionService` is deliberately fail-loud, and the
  credential resolver (`:241`, `:270`) wraps its appends in **neither**. Since this story models itself on
  that resolver, it must choose consciously rather than copy: D7 picks swallow-and-warn for
  `TOOL.SECRET_ACCESSED`, because an event-store hiccup must not fail a tool call that already succeeded.
- **Y6 — only the BYOK leg of `DefaultProviderCredentialResolver` is cached** (`:52`, write at `:114`); the
  platform leg relies on `RuntimeSecretResolver`'s own 60 s cache (`:28`). A generalization that caches
  uniformly is a *change*, not a copy — D4 states which.

## Design Decisions

- **D1 — `IToolSecretProvider` and its implementation both live in `Tamma.Api`; the interface is not put on
  the engine surface "just in case".** There is no engine-side consumer: every external-touching executor is
  Api-side (Epic 42 README "Where the code lives"), and `Tamma.Api` is deliberately excluded from
  `Allowlist.IsEngineSurface` (`:16-17`). Note the precedent's shape, which the story reads slightly
  optimistically: `IProviderCredentialResolver`'s **interface** lives in `Tamma.Activities`
  (`LlmCall/Credentials/`) with its impl in `Tamma.Api` — and precisely because the interface is visible to
  the engine, it had to be added to `InjectionDenylist` (`:59`) to stop the engine injecting it. Siting
  `IToolSecretProvider` wholly in `Tamma.Api` avoids needing a denylist entry at all. If a future engine-side
  consumer appears, the interface moves to `Tamma.Activities` **and must be added to `InjectionDenylist` in
  the same commit** — recorded here so that is not forgotten.
- **D2 — the provider constructs the ref; it never accepts one.** The single method takes the run's tenant
  identity and the descriptor's requirement, and builds the `SecretRef` itself:

  ```csharp
  // namespace Tamma.Api.Services.Tools.Secrets
  public interface IToolSecretProvider
  {
      Task<ToolCredential> ResolveAsync(Guid? runTenantId, SecretRequirement requirement, CancellationToken ct);
      void Invalidate(Guid? runTenantId, string name);
  }
  public sealed record ToolCredential(string Plaintext, string SecretRefStorageKey, int? VersionNumber)
  {
      public ToolCredentialTag ToTag() => new(SecretRefStorageKey, VersionNumber);
  }
  public sealed record ToolCredentialTag(string SecretRef, int? Version);   // the ONLY thing logs/events see
  ```

  There is **no overload accepting a `SecretRef`** — that absence is the cross-tenant control, and it is
  asserted structurally (AC1(d)), not by a runtime check. This is load-bearing because `ISecretStore`
  performs no authorization: `SecretStore`'s ctor injects a db-context factory, a backend, an auditor, a
  `TimeProvider` and a logger — **no caller identity of any kind** — and audits with actor `Guid.Empty`. It
  will serve whatever ref it is handed. Isolation is this story's obligation, inherited from nothing.
- **D3 — resolution happens inside `ExecuteAsync`, immediately before the external call, and a missing
  `Required` secret is a fail-closed `ToolExecutionResult`.** Per the Reconciled scope: 42-3's resolve-time
  pre-screen does not exist. This is not a downgrade — §3's never-hold rule already required the fetch to
  happen immediately before the vendor call, so there was never a second, earlier fetch to fail at. The
  contract: resolve → on failure return `Success = false` with a typed, redacted message and **zero** calls
  on the vendor driver (asserted on a spy, not on the message text) → on success use it for exactly one call
  → drop it. `ExecuteAsync` still never throws (`IToolExecutor.cs:8`, `:33`).
- **D4 — cache the platform leg too, uniformly, 60 s, with explicit `Invalidate`.** Y6: the precedent caches
  BYOK only and leans on `RuntimeSecretResolver` for platform. This story has no `RuntimeSecretResolver` in
  its path (it reads through `SecretStorePlatformCredentialReader`), so it must cache both legs itself or
  hit the database on every tool call. TTL is `RuntimeSecretResolver.DefaultCacheTtl` (`:28`) reused, not a
  new constant. Cache key is `(tenantId, purpose, name)`; **the cached value is the plaintext**, so the cache
  is a deliberate, bounded exception to never-hold — bounded to the provider's own field, never handed
  outward except through `ResolveAsync`, and invalidatable. Say so explicitly rather than pretending the
  provider is stateless.
- **D5 — the provider audits its own rejections, closing Y2's gap.** Every `ResolveAsync` outcome emits: a
  `TOOL.SECRET_ACCESSED` DCB row **and** an `ISecretAccessAuditor.EmitAsync` call — including the
  argument-rejection paths (`Tenant` scope with a null tenant id; a requirement naming a tenant other than
  the run's). `SecretStorePlatformCredentialReader` does not audit those (`:66`-`:85`), so inheriting its
  behaviour would leave the cross-tenant attempt — the single most security-relevant event this story can
  emit — unrecorded.
- **D6 — AC6 is rewritten to something testable.** Original: *"the tool is not offered to the agent and the
  step routes human-assigned."* No mechanism exists to un-offer a tool (42-3 deleted; Epic 43's Seam B is
  invocation-time and does not consult secret availability). **Rewritten AC6:** with the secret provider
  stubbed unavailable, the tool is still offered, its `ExecuteAsync` returns `Success = false` with a typed
  "capability unconfigured" message, **zero** vendor calls occur, one `TOOL.FAILED` row is written, and the
  agent run continues (the model sees the failure as a tool result, the existing rejected-tool-call machinery
  path). The "route human-assigned" behaviour is filed to Epic 43 as a follow-on, not silently claimed.
- **D7 — `TOOL.SECRET_ACCESSED` is appended directly via `IEventRepository`, swallow-and-warn.** The tool
  loop does not run in the engine, and `TammaEventEmitter` structurally requires an `ActivityExecutionContext`
  plus an `IActivity` (`Tamma.Activities/Core/TammaActivity.cs:82-147`) and writes only to transient workflow
  properties — never to the store. `Tamma.Api` holds `IEventRepository` directly. Per Y5, the failure policy
  is chosen, not copied: swallow-and-warn (the `AlertEventEmitter.cs:81-86` posture), because a credential was
  already fetched and the call already made — failing the tool on an audit-write hiccup would be worse than
  the missing row. Note `AppendAsync` takes **no `CancellationToken`** (`IEventRepository.cs:7`), the
  constraint `AlertEventEmitter.cs:88` documents.
- **D8 — the redaction split is stated as one sentence and enforced in two places.** *"By-value at the
  `ExecuteAsync` boundary; never-hold + pattern after it."* Inside the executor — which legitimately holds the
  plaintext, having just authenticated with it — the value is replaced by string match in anything returned:
  `Output`, echoed headers, status text, error messages (42-9 S10 is the sharp case). After `ExecuteAsync`
  returns, nothing is ever handed the value, so downstream uses **pattern** redaction only:
  `ToolOutputHelper.RedactSecrets` (`:72-120`, already applied on both loop branches) and
  `CredentialRedactor.Clean` (`Tamma.Core/Redaction/CredentialRedactor.cs:71`). Both are pattern-based and
  match **no arbitrary bound token** — stated plainly so nobody mistakes them for a value-match backstop.
- **G1 — the logical secret name has no per-principal override, and that is a recorded gap.** 42-2 would have
  let a `tenant_admin` point a tool at a differently-named secret (`secret_binding_name`). Epic 43's
  `action_assignments` stores only policy — three nullable columns and a threshold — with no `ConfigJson` and
  no secret-name column. So after reconciliation the name is **whatever the descriptor hardcodes**, e.g.
  `"cloud/<provider>-token"`. That is workable in single-user and for a single tenant convention, and it is a
  real limitation in SaaS. **This plan does not invent a replacement store** (that would recreate exactly the
  duplication the reconciliation removed). It resolves the name through a small
  `IToolSecretNameResolver` seam with one shipped implementation returning
  `requirement.Name` verbatim, so a future owner (Epic 43, or a revived binding store) has one place to
  plug in. Flagged here and in Blocks / Blocked by as an open product question.

## Implementation Steps

1. **Precondition gate (no code).** 42-1 landed: `ToolDescriptor(RequiredSecret, Suspends)` and
   `SecretRequirement(Purpose, Name, Required)` compile, and `SecretPurpose` is reachable from
   `Tamma.Activities` (42-1 D1 decides which namespace to `using`).
2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tools/Secrets/IToolSecretProvider.cs`** — D2's interface,
   `ToolCredential`, `ToolCredentialTag`; plus `IToolSecretNameResolver` + `DefaultToolSecretNameResolver`
   (G1).
3. **CREATE `.../Secrets/ToolSecretProvider.cs`** — the implementation: per-mode ref construction (D2),
   the 60 s two-leg cache + `Invalidate` (D4), reads through
   `SecretStorePlatformCredentialReader.ReadActivePlaintextAsync`, fail-closed `TammaError`
   `TOOL.SECRET_UNAVAILABLE` (`retryable: false`, `severity: High`), and the tag projection.
4. **CREATE `.../Secrets/ToolSecretAuditEmitter.cs`** — D5/D7: one `TOOL.SECRET_ACCESSED` DCB append per
   `ResolveAsync` outcome (success and every rejection) plus the `ISecretAccessAuditor.EmitAsync` call.
5. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — register `IToolSecretProvider` next to the tool
   wiring (`:753-766`), scoped, conditional on the secrets cabinet being present exactly as
   `SecretStorePlatformCredentialReader` is (`:849-857`), with a null-object fallback that fails closed.
6. **CREATE the test suites** (Test Plan).
7. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean (no schema);
   confirm no `Tamma.Activities` file references `IToolSecretProvider` and that
   `Allowlist.InjectionDenylist` needed no new entry (D1).

## Data & Migrations

None. Secrets live in the existing `secrets` table (Epic 29's migration); `TOOL.SECRET_ACCESSED` rides the
existing `IEventRepository` → `domain_events` path.

## Events

- **Emits:** `TOOL.SECRET_ACCESSED` — tags `secretRefStorageKey` (via `SecretRef.ToStorageKey()`, `:60`),
  `purpose`, `tenantId`, `toolName`, `correlationId`; data `outcome`, `versionNumber`, `cacheHit`. **Never a
  value.** One row per `ResolveAsync`, including rejections (D5).
- **Calls (not an event):** `ISecretAccessAuditor.EmitAsync` with `SecretAuditEventTypes.Read` — which today
  lands in `NullSecretAccessAuditor` (`ISecretAccessAuditor.cs:118-123`,
  `SecretsServiceCollectionExtensions.cs:56`). The DCB row is therefore **the** load-bearing trail; no AC may
  assert a persisted `SECRET.READ`.

## Test Plan

- **`ToolSecretProviderScopeTests`** — (a) single-user (`runTenantId == null`) yields exactly
  `SecretRef.ForPlatform(name)`; SaaS yields exactly `SecretRef.ForTenant(runTenantId, name)`; (b)
  constructing a `Tenant`-scoped ref with a null tenant id throws `ArgumentException` (`SecretRef.cs:37-40`);
  (c) a requirement naming a tenant other than the run's is **rejected before any read** — asserted against
  the *provider* with a spy reader at zero calls, because `ISecretStore` would happily serve it; (d) a
  **reflection** assertion that no public member of `IToolSecretProvider` accepts a `SecretRef` parameter
  (D2's structural control). **Covers AC1.**
- **`ToolSecretLifetimeTests`** — `ResolveAsync` returns a credential whose only outward projection is
  `ToTag()`; a reflection assertion that `ToolCredentialTag` exposes no member carrying the plaintext; the
  cache honours the 60 s TTL and `Invalidate` (D4), and a test documents that the plaintext **is** held in the
  provider's cache for that window (the honest statement of D4's exception). **Covers AC2.**
- **`ToolSecretFailClosedTests`** — a missing/inactive/scrubbed secret yields the typed
  `TOOL.SECRET_UNAVAILABLE`; wired through a fake external-touching executor, the vendor spy records **zero**
  calls and `ExecuteAsync` **returns** `Success = false` rather than throwing. **Covers AC3, rewritten AC6.**
- **`ToolSecretRedactionTests`** — seed a random 40-char token (deliberately matching **no**
  `ToolOutputHelper.RedactSecrets` pattern, which is the whole point of D8's by-value half); run a family tool
  that echoes it; grep for the literal across `ToolExecutionResult.Output`, every emitted DCB event's `Tags`
  and `Data`, and all captured log lines. Plus the **structural** assertion that no emitter or audit method
  accepts a plaintext parameter — never-hold is a signature property, not a string search. **Covers AC4.**
- **`ToolSecretAuditTests`** — exactly one `TOOL.SECRET_ACCESSED` row per fetch carrying
  `secretRefStorageKey` + `purpose` + `tenantId` and no value; exactly one `ISecretAccessAuditor.EmitAsync`
  via a **capturing fake** (never asserting a persisted `SECRET.READ` — the registered auditor discards);
  **and one row for each rejection path** (D5/Y2), including the cross-tenant attempt; a throwing
  `IEventRepository` does not fail the resolve (D7/Y5). **Covers AC5.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — scope resolution per mode; no caller-built refs; cross-tenant rejected before any read | 2, 3 (D2) | `ToolSecretProviderScopeTests` (a)–(d) |
| 2 — short-lived credential; only the tag projection escapes | 3 (D4) | `ToolSecretLifetimeTests` |
| 3 — unconfigured `Required` secret fails loud, zero vendor calls | 3 (D3) | `ToolSecretFailClosedTests` |
| 4 — no plaintext in output, events, or errors | 3 (D8) | `ToolSecretRedactionTests` (grep + structural) |
| 5 — every fetch audited (DCB row + auditor call) | 4 (D5/D7) | `ToolSecretAuditTests` |
| 6 — **rewritten**: unavailable provider degrades to a fail-closed tool result, never a crash or an unauthenticated call | 3 (D3/D6) | `ToolSecretFailClosedTests` |
| ~~"not offered to the agent" / human-assigned routing~~ | — | **Out of scope — no mechanism exists post-reconciliation (D6); filed to Epic 43** |

## Blocks / Blocked by

- **Blocked by — 42-1** (`SecretRequirement` on the descriptor and the `SecretPurpose` relocation; without
  them the descriptor cannot name a purpose at all). Hard.
- **Blocked by — Epic 29 (satisfied, with one caveat).** `ISecretStore`, `SecretRef`, `SecretScope`,
  `SecretPurpose`, `SecretsDbContext` and all four runtime plaintext readers ship and are verified in tree.
  The caveat is **not** blocking: `ISecretAccessAuditor` has exactly one implementation and it discards
  (`SecretsServiceCollectionExtensions.cs:56`), so audited-read acceptance criteria **currently land
  nowhere**. AC5 is written against a capturing fake and the DCB row precisely so this story is testable and
  honest today; wiring a real auditor is an Epic 29 follow-on this epic depends on and does not build.
- **No longer blocked by — 42-2 / 42-3** (deleted). See Reconciled scope; G1 is the residue.
- **Open product question — G1.** Who owns the per-principal secret *name* now that 42-2 is gone? Epic 43's
  `action_assignments` does not model it. Options: leave it descriptor-hardcoded (this plan's default, via a
  one-line `IToolSecretNameResolver`); add a name column to Epic 43's store; or revive a minimal
  tool-config store. **Not decided here** — it is a product/architecture call, and deciding it inside a
  story plan is how the duplication the reconciliation removed got created in the first place.
- **Blocks — 42-7, 42-8A, 42-8B, 42-9** (every external-touching family's agent path) and **42-6 Part B**
  (an MCP server's bound auth secret). Nothing else in the epic depends on it.

## Risks & Mitigations

- **`ISecretStore` looks authoritative and performs no authorization.** A reviewer reading its XML doc will
  believe otherwise; its ctor injects no caller identity and it audits with actor `Guid.Empty`. Mitigation:
  isolation is pinned on the *provider* (AC1(c)) and the provider structurally refuses caller-built refs
  (AC1(d)) — a runtime check alone would be one refactor away from being bypassed.
- **The audit trail is a no-op today (Y3).** An implementer can "pass" an audit test that asserts nothing.
  Mitigation: AC5 asserts the DCB row (real, persisted) **and** the auditor call via a capturing fake, and
  explicitly forbids asserting a persisted `SECRET.READ`.
- **D4's cache holds plaintext, contradicting the never-hold headline if read carelessly.** Mitigation: the
  exception is stated in D4, bounded to the provider's own field, TTL'd, invalidatable, and pinned by a test
  that documents rather than hides it. The alternative — a database round trip per tool call — is worse and
  would still hold plaintext, just more often.
- **Pattern redaction is not DLP (D8).** `ToolOutputHelper.RedactSecrets` matches `sk-`, `AKIA`, `gh?_`,
  `glpat-`, `xox?-`, JWT, PEM, `Password=` and similar — **not** an arbitrary bound token. Mitigation: the
  by-value half runs inside the executor where the plaintext genuinely is; the never-hold guarantee makes
  the bound secret structurally unreachable downstream; the AC4 test deliberately uses a
  pattern-non-matching token so a regression cannot hide behind a lucky regex.
- **Assembly drift.** A contributor sites a credential-resolving tool in `Tamma.Activities` because the six
  built-ins live there. Mitigation: D1's placement rule, the `TAMMA001` precedent at `Allowlist.cs:59`, and
  step 7's explicit check. Note honestly that `TAMMA001` would **not** mechanically catch it — the denylist
  is a closed 13-entry list that does not name `IToolSecretProvider`, and its HTTP check fires only on a
  statically-literal external host. The analyzer is a backstop, not the enforcement.
- **G1 leaves SaaS name-binding with no story to build it.** Mitigation: the one-line resolver seam gives a future story a
  single plug point; the gap is recorded in Blocks / Blocked by rather than papered over with a new table.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition gate + interface, credential/tag records, name-resolver seam | 0.5 |
| 3 | `ToolSecretProvider` (per-mode refs, two-leg cache, fail-closed, tag projection) | 1.0 |
| 4–5 | Audit emitter (incl. rejection paths) + DI wiring with the null fallback | 0.5 |
| 6 | Five test suites incl. the structural/reflection and grep-for-value cases | 1.0 |
| 7 | Full green + siting checks | 0.25 |
| **Total** | | **3.25** (story estimate: ~3 days) |

The reconciliation neither added nor removed work here — it removed a *dependency* (42-2's override) and
relocated a *failure surface* (42-3's resolve-time screen), which D3/D6/G1 absorb at roughly zero net cost.
