# Story 42-4: Tool Credential / Secret Binding

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **tool that touches an external system**, I want to **bind to a stored secret** resolved for my
principal — tenant-scoped in SaaS, platform-scoped in single-user — and receive the live credential at
execution time **without it ever reaching a log, an event, or the tool's output**, so that a
cloud/flag/deploy/HTTP tool authenticates safely and the platform's no-secret-in-logs promise holds for
capabilities the same way it holds for documents.

## Priority

P0 / Wave 1 — the security envelope for every external-touching family. 42-7/8/9 cannot ship their agent
path without it.

*Corrected:* earlier drafts called this "the epic's biggest external dependency" and filed a blocking
Epic 29 story. It is not blocked — the runtime-reveal capability it needed **already ships four times
over** (below). This story **generalizes a landed seam**; it does not wait on one.

## The gap (READ FIRST)

No tool binds to a secret today; the six built-ins touch only the local repo/git/shell. What must be
built is a *tool-facing* resolver, not a new reveal capability.

**Corrected — "the reveal-to-runtime path does not exist" was false.** Epic 29's `ISecretStore` exists
(Story 29-1; CLAUDE.md's "does not yet exist" note is stale) with `SecretRef` (`scope`, `tenantId?`,
`name`), `SecretScope`, `SecretPurpose`, an envelope-encrypted Postgres backend, and an
`ISecretAccessAuditor` seam. It is true that **no `ISecretStore` method returns plaintext** — all seven
return `SecretMetadata` / `SecretVersion`, neither of which carries bytes. But **four runtime plaintext
readers already ship around that boundary**, all in `Tamma.Api`:

| Reader | Signature | Notes |
|---|---|---|
| `SecretStorePlatformCredentialReader` (`Services/Platforms/`) | `ReadActivePlaintextAsync(string scope, Guid? tenantId, string name, ct)` | Both scopes; validates the scope/tenant invariant up front; emits `SecretAuditEventTypes.Read` through `ISecretAccessAuditor` on **every** success and failure branch. **The model for this story.** |
| `CabinetTenantProviderKeyReader` (`Services/Providers/`) | `TryReadAsync(Guid tenantId, string cabinetName, ct)` | EF predicate pins `Scope == "tenant" && TenantId == tenantId`; degrades to null on probe failure. **The model for tenant pinning.** |
| `RuntimeSecretResolver` (`Services/Secrets/Stopgap/`) | `GetAsync(string cabinetName, ct)` | Platform-only (`s.Scope == "platform"` hard-coded), 60 s cache + `Invalidate`, fail-closed via `MissingSecretException`. **Unaudited** — injects no auditor. |
| `IAlertChannelSecretReader` (`Services/Alerts/`) | `GetPlaintextAsync(Guid secretId, ct)` | By-id reader for alert channels. |

On top of them, `IProviderCredentialResolver` / `DefaultProviderCredentialResolver` is a **complete
working precedent** for exactly this story's shape: BYOK→platform precedence, 60 s cache, a `ToTag()`
projection that is the only thing allowed into a log/event/diagnostic, and a fail-closed
`TammaError("PROVIDER_CREDENTIAL_UNAVAILABLE", retryable:false, severity:High)`. It is *not* a
substitute — it is keyed on an allowlisted `providerName` with fixed cabinet slugs
(`provider/<n>/api-key`, `<n>/api-key`) and only `Byok|Platform` sources, so it cannot resolve an
arbitrary `SecretRequirement(Purpose, Name)`.

**Corrected — `ISecretStore` performs no authorization.** `SecretStore`'s constructor injects only a
`IDbContextFactory<SecretsDbContext>`, `ISecretStoreBackend`, `ISecretAccessAuditor`, `TimeProvider`
and a logger — **no caller identity of any kind** — and `GetAsync` audits with actor `Guid.Empty`. The
interface doc's "a row the caller is authorised to see" is aspirational. The store resolves **whatever
`SecretRef` the caller hands it**. Cross-tenant isolation is therefore *this story's* obligation, not
an inherited guarantee.

**The two gaps that are real** (they replace the phantom one):

1. **The audit sink is a no-op.** The only `ISecretAccessAuditor` implementation in the repo is
   `NullSecretAccessAuditor`, registered by `TryAddSingleton` under the comment *"Audit pipe — null
   until a future story wires the real one."* Every `SECRET.READ` emitted today is dropped on the floor.
2. **The engine surface may not hold a credential resolver.** `Allowlist.InjectionDenylist` (the
   `TAMMA001` guardrail, `DiagnosticSeverity.Error`) lists
   `Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver`, and `IsEngineSurface` covers
   `Tamma.Activities` / `Tamma.ElsaServer`. That is the standing architectural rule this story must not
   violate.

## Scope

**Assembly placement (binding).** `IToolSecretProvider` and its implementation both live in
**`Tamma.Api`** — there is no engine-side consumer, every external-touching executor already lives
there (README "Where the code lives"), and `Tamma.Api` is deliberately excluded from
`Allowlist.IsEngineSurface`. Only 42-1's `SecretRequirement` (`{ Purpose, Name, Required }`) stays on
the contract surface in `Tamma.Activities.LlmCall.Tools`, with its `Purpose` typed by **Epic 29's
`SecretPurpose` after 42-1 §0 relocates the enum to `Tamma.Core.Enums`** — it ships today in
`Tamma.Api.Services.Secrets` and is unreachable from `Tamma.Activities` (the reverse project reference
is circular). It is a **move, not a mirror**: this story maintains no parallel taxonomy and no mapping
table, and names `Tamma.Core.Enums.SecretPurpose` directly.
`SecretRef` / `SecretScope` are likewise Api-side types: **a `ToolDescriptor` never
carries a `SecretRef`**, only the logical requirement. If a future engine-side consumer ever appears,
the interface moves to `Tamma.Activities` with the impl staying in `Tamma.Api` (the
`IProviderCredentialResolver` split) — and must then be added to `InjectionDenylist`.

1. **Resolve a tool's `RequiredSecret` to a `SecretRef` per mode.** 42-1's descriptor declares
   `SecretRequirement(Purpose, Name, Required)`; 42-2's binding may override the logical `Name`
   (`secret_binding_name`). This story maps that to a concrete ref:
   - **single-user:** `SecretRef.ForPlatform(name)`. *Corrected:* there is **no user scope** —
     `SecretScope` has exactly `Platform` and `Tenant`, and `SecretRef`'s constructor throws on either
     mismatch (`Platform` + non-null tenant, `Tenant` + null tenant), an invariant re-enforced by
     `SecretMetadataFactory.ValidateScopeTenant` and by `SecretStorePlatformCredentialReader`. The sole
     user's ownership is recorded as **metadata** on `SecretMetadata.OwnerUserId`, never as a scope.
     (42-2's `tool_bindings` legitimately keys on `user_id`; the *secret* it points at is still a
     platform ref.)
   - **SaaS:** `SecretRef.ForTenant(runTenantId, name)` where `runTenantId` comes from the **run
     context**, never from tool config, tool arguments, or the LLM.
   A `Required` secret that does not resolve is a **loud, typed** "capability unconfigured" failure at
   resolve time (42-3 surfaces it) — never a tool that runs unauthenticated.

2. **`IToolSecretProvider` — generalize the shipped seam.** *Corrected: this is no longer "the hard
   dependency". The epic records this as **decision D1** ("Secret reveal for runtime tool execution:
   option (a), and it is not a new capability") — it was an open question in an earlier draft and is
   now settled by the code, because option (a) is what already ships.* Model
   it on `IProviderCredentialResolver`/`DefaultProviderCredentialResolver`, backed by
   `SecretStorePlatformCredentialReader.ReadActivePlaintextAsync` (the one **audited**, scope-generic
   reader). Required properties, each inherited from a landed precedent:
   - resolves an arbitrary `(Purpose, Name)` — the generalization over the provider-name-keyed resolver;
   - **the caller supplies and proves the scope**: the provider takes the run's tenant identity as an
     argument and constructs the ref itself; it never accepts a caller-built `SecretRef`;
   - short TTL cache + explicit `Invalidate` (`RuntimeSecretResolver` / `DefaultProviderCredentialResolver`);
   - a `ToTag()`-style projection (`{ source, secretRefStorageKey, version }`) that is the **only**
     thing any log/event/diagnostic ever receives (`ProviderCredential.ToTag()`);
   - **fail-closed** with a typed `TammaError` — never an empty or wrong credential.

3. **Never-hold, not scrub-later.** The credential is fetched immediately before the external call, used
   for that call, and dropped — the pattern `ManagedAgent` already uses for the request-scoped provider
   key. It is never placed in `ToolExecutionResult.Output`, never passed to the 42-5 audit emitter, and
   never interpolated into an error message. *Corrected:* earlier drafts told 42-5 to redact **events**
   with a **value-match denylist** against resolved secret values. That is retired at the audit
   boundary — value matching there requires holding plaintext at a boundary this story forbids it to
   reach. The boundary defence is instead (a) plaintext never crosses into the emit path, (b) tag
   projection, and (c) **pattern** redaction of anything that does cross — the landed
   `ToolOutputHelper.RedactSecrets` (already applied to every tool output on both loop branches) and
   `CredentialRedactor.Clean` (`Tamma.Core.Redaction`, already applied before DCB persistence
   elsewhere).

   **Where by-value redaction *is* required — inside the executor.** The two rules are about two
   different places and must not be conflated (42-9 S10 mandates by-value scrubbing; this story
   forbids it downstream). The line is: **the executor already holds the plaintext** — it just used it
   for the external call — so it must scrub the credential **by value** from anything it *returns*
   (`ToolExecutionResult.Output`, an echoed header, a status line, an error message) **before**
   `ExecuteAsync` returns. Everything downstream of that return — the 42-5 emitter, the DCB row, the
   `ToolAuthorizationRequest`, the log — is never handed the value and therefore cannot value-match,
   and must not try. In one sentence: **by-value at the `ExecuteAsync` boundary, never-hold + pattern
   after it.** Families that echo an endpoint's response (42-9 in particular) carry the by-value half;
   the emit path carries only the pattern half.

4. **Audit the fetch — with the sink's real state stated.** Every tool secret fetch calls
   `ISecretAccessAuditor` (via the reader) **and** appends a DCB `TOOL.SECRET_ACCESSED` event carrying
   the ref storage key + purpose + tenant tag, never the value. Because the only registered auditor is
   `NullSecretAccessAuditor`, the **DCB event is the load-bearing trail** for this epic; wiring a real
   `ISecretAccessAuditor` is an Epic 29 follow-on that this story records but does not own.

## Acceptance Criteria

1. **Scope resolution per mode.** Single-user resolves `SecretRef.ForPlatform(name)`; SaaS resolves
   `SecretRef.ForTenant(runTenantId, name)`. Tests: (a) both refs are exactly as stated; (b)
   constructing a tenant-scoped ref with a null tenant id throws `ArgumentException`; (c) a binding or
   tool argument naming a tenant id other than the run's is **rejected before any read** with a typed
   failure — asserted against the *provider*, because `SecretStore` would serve it (it holds no caller
   identity); (d) the provider exposes no overload that accepts a caller-constructed `SecretRef`.
2. **Short-lived credential.** `IToolSecretProvider` returns a credential for one invocation; a test
   asserts the value is not retained after `ExecuteAsync` returns, and that the type handed to any
   log/event/diagnostic is the tag projection (no member exposing the plaintext).
3. **Unconfigured `Required` secret fails loud.** A missing/inactive secret produces a typed
   `TammaError` at resolve time and the executor's `ExecuteAsync` is never entered (test asserts zero
   invocations, not merely a failed result).
4. **No plaintext anywhere.** A test seeds a known credential-shaped value, runs a family tool, and
   greps `ToolExecutionResult.Output`, every emitted DCB event's `Tags`+`Data`, and the error text for
   it — plus a structural assertion that the 42-5 emit call site is never handed the credential
   (signature-level, not string-level).
5. **Fetch is audited.** Every fetch appends exactly one `TOOL.SECRET_ACCESSED` DCB row carrying
   `secretRefStorageKey` + `purpose` + `tenantId` and **no** value, and invokes `ISecretAccessAuditor`
   once. The test asserts the DCB row (real) and the auditor call (via a capturing fake) — it must not
   assert a persisted `SECRET.READ` row, because the registered auditor is `NullSecretAccessAuditor`.
6. **Degradation, not steady state.** With the secret provider stubbed unavailable, an
   external-touching tool is not offered to the agent and the step routes human-assigned (Epic 41
   rule 4) — never a crash, never a silent unauthenticated call. *This is a resilience test, not the
   expected operating mode: the reveal path exists.*

## Events

`TOOL.SECRET_ACCESSED` (ref storage key + purpose + tenant tag, **no value**). All other tool events are
42-5; this story defines only the secret-access one and the never-hold/tag-projection/pattern-redaction
rules the rest inherit.

## Single-user vs SaaS

- **single-user:** the tool resolves `SecretRef.ForPlatform(name)`; the sole user's ownership is
  recorded on `SecretMetadata.OwnerUserId`, not as a scope. This mirrors what already ships —
  `DefaultProviderCredentialResolver` treats `tenantId == null` as "no BYOK layer, go straight to the
  platform key."
- **SaaS:** `SecretRef.ForTenant(runTenantId, name)`, owned by `tenant_admin`; a `member`-run agent uses
  the tenant's bound secret without seeing it. *Corrected:* isolation is **not** enforced by
  `ISecretStore` — it performs no authorization. It is enforced here, by pinning the run's tenant id
  into the ref (the `CabinetTenantProviderKeyReader` pattern) and by AC1(c)/(d).

## Dependencies

- **Epic 29 (soft, not blocking):** `ISecretStore` / `SecretRef` / `SecretScope` / `SecretPurpose` /
  `ISecretAccessAuditor` and the four runtime readers all exist. Two follow-ons are noted, neither
  blocking: a real `ISecretAccessAuditor` implementation (only `NullSecretAccessAuditor` is registered),
  and re-pointing the direct-seam readers at the `SecretStore` facade (an Epic 29 cleanup already
  flagged in `SecretStore`'s own docs).
- **42-1** — `SecretRequirement` on the descriptor **and its §0 relocation of the purpose enum to
  `Tamma.Core`**; without that, the descriptor cannot name a purpose at all.
- **42-2** (`secret_binding_name` override), **42-5** (inherits the redaction rules; is never handed a
  credential).
- **Guardrail:** `Tamma.Activities` carries the `TAMMA001` analyzer and holds no external credential —
  credential-holding code must not be added to it.
- **Unblocks:** 42-7/8/9 agent paths. *Corrected: they are no longer waiting on an unlanded Epic 29
  capability; they wait only on this story.*

## Risks

- **`ISecretStore` looks authoritative and is not.** A reviewer reading `ISecretStore`'s XML doc will
  believe the store authorizes. It does not. Mitigation: AC1(c)/(d) pin the isolation test on the
  provider, and the provider refuses caller-built refs.
- **Assembly drift.** A future contributor sites a credential-resolving tool in `Tamma.Activities`
  because the six built-ins live there. Mitigation: the placement rule above + the `TAMMA001` denylist
  precedent for `IProviderCredentialResolver`.
- **Leak surface breadth.** Secrets can leak via output, events, errors, or a mis-typed `config` blob
  (42-2). Mitigation: never-hold + tag projection at the boundary, pattern redaction
  (`ToolOutputHelper.RedactSecrets`, `CredentialRedactor.Clean`) on anything that does cross, and AC4's
  structural + grep test run against every family.
- **Silent audit.** With `NullSecretAccessAuditor` registered, an implementer can "pass" an audit test
  that asserts nothing persisted. Mitigation: AC5 asserts the DCB row and a capturing fake explicitly.

## Estimated Effort

Medium. ~3 days — a generalization of a shipped seam (resolver + cache + tag projection + fail-closed)
plus the redaction/fallback tests. *Corrected: earlier estimate was "Large (blocked-dependency)" with an
Epic 29 story carved out; no such carve-out is needed.*
