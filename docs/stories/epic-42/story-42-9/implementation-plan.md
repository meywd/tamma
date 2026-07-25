# Implementation Plan — Story 42-9: Authenticated HTTP / External-API Tool

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** The verdict for 42-9 (with 42-7 / 42-8A / 42-8B) is
**"Gating sections stripped. They declare capability and secrets; the catalog governs them."** 42-9 is the
family **most** disturbed by the reconciliation, because the deleted 42-2 was not merely its policy source —
it was its *destination* source. The deltas:

| Story file says | Reconciled |
|---|---|
| §2's per-call `PermissionClass` table and `ToolInvocationFacts Describe(string argumentsJson)` | **STRIPPED.** 42-3 is deleted; nothing consumes `ToolInvocationFacts`. `ToolPermissionClass` no longer exists (42-1 rewritten; `ToolDescriptor` is `(RequiredSecret, Suspends)`). Epic 43's **Seam B** gates on an `ActionKey` derived from the tool name. |
| **§1 / §2 / Dependencies — "the 42-2 binding's `ConfigJson` carries base URI, host-suffix allowlist, method allowlist, auth mode, header-name allowlist, request/response caps and the `destructive` marker. Without it this tool has no destination and no class."** | **42-2 is DELETED and Epic 43's `action_assignments` stores policy only** — a threshold plus three nullable columns, no config blob. So the story's own warning has come true: **as written, this tool has no destination.** Resolved by **D2**: endpoint bindings move to deployment configuration (`IOptions`), platform-scoped and startup-validated. **This is the largest single reconciliation delta in Epic 42** and it changes what the story can promise in SaaS — see **G1**. |
| AC1's `Destructive` / floor 100 descriptor | **STRIPPED.** AC1 becomes: `HttpRequestTool` registered, `Suspends = false`, declaring `SecretRequirement(ApiKey, "http/<endpoint-key>", Required)`. |
| AC2 (`Describe` table), AC3 (`ToolAuthorizationRequired` + `ToolAuthorizationRequest`), AC6's "42-3 decision id" | **STRIPPED.** |
| **S11.3** — "a binding may set `destructive: true` purely to force human review of its *content*: the `ToolAuthorizationRequest` then carries the redacted body, and a test asserts an operator sees it before the send" | **STRIPPED** — there is no `ToolAuthorizationRequest`. Epic 43's Seam B outcome is named `Denied`, **not** `RequiresHuman`, precisely because *"there is no human on that path and calling it escalation would be a lie."* So the content-review affordance S11 relied on **does not exist**, and S11's residual widens. See **T2** — this is an honesty correction, not a scope cut. |
| The "Stage-1 filter vs. max-class descriptor" risk (called "the hardest dependency here") | **Gone.** Epic 43 records the same insight in its Seam B analysis, with credit. |

**Unchanged and still the bulk of the work:** the entire **S1–S10 SSRF / egress containment matrix**, the
`JiraBaseUrlGuard` extraction with its two deliberate divergences, response caps and timeouts, and the
by-value credential scrubbing. Those were always ~80% of this story and the reconciliation did not touch
them.

## Scope & Deliverable

One `HttpRequestTool : IToolExecutor` in `Tamma.Api` making exactly one authenticated REST call to a
**configuration-resolved** endpoint. The model supplies an `endpointKey` from a closed declared set plus a
relative `path`, `query`, `body` and a restricted set of `headers`; **there is no URL, scheme, host or
authority parameter in `InputSchema`**. The absolute URI is composed server-side. Egress is contained by a
shared SSRF guard extracted from `JiraBaseUrlGuard`, running the private/loopback/link-local/metadata floor
at **validation and at connect**, allowlisted or not, with redirects disabled. The credential binds through
42-4, is applied in the declared auth mode, and is scrubbed **by value** from everything the executor
returns. Every call emits 42-5's `TOOL.*` trio with `endpointKey` / method / composed path / status /
request-byte-size / body digest — never a query string verbatim, never a header value, never the credential.

## Pre-Reading

- `docs/stories/epic-42/story-42-9/42-9-authenticated-http-external-api-tool.md` — the story (**read the Reconciled scope table first**; S1–S10 survive verbatim and are the deliverable)
- `docs/stories/epic-42/README.md` — the verdicts; "Where the code lives"; the families table
- `docs/stories/epic-43/README.md` — **Enforcement** (Seam B, and the reasoning behind the `Denied`-not-`RequiresHuman` naming — the evidence for T2) and **Storage** (`action_assignments`' columns — the evidence for G1)
- `docs/stories/epic-42/story-42-1/implementation-plan.md` (D2/D8), `story-42-4/implementation-plan.md` (**D2/D3/D8 — the by-value/pattern split this story's S10 is the sharp case of; and G1**), `story-42-5/implementation-plan.md` (D2/D3/D5), `story-42-8/implementation-plan.md` (**C2 — why this story's position in the wave order is now uncertain; C3 — the shared missing store**)
- **`apps/tamma-elsa/src/Tamma.Api/Services/Integrations/JiraBaseUrlGuard.cs`** — `public static class` `:34`; **`ValidateAsync` `:46`** with the allowlist short-circuit around `:72-95` and the all-or-nothing check **`:113`** (`addresses.Any(IsBlockedAddress)` → reject); **`IsBlockedAddress` `:135`**; **`SafeConnectAsync` `:186`** with the **filtering** line **`:197`** (`addresses.Where(a => !IsBlockedAddress(a))`); `JiraBaseUrlValidation` `:246-249`
- **`apps/tamma-elsa/src/Tamma.Api/Program.cs:184-189`** — the shipped handler wiring, verbatim:
  `AddHttpClient(JiraApiClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, ConnectCallback = JiraBaseUrlGuard.SafeConnectAsync })`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-766` — where the executor registers; `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs:286-292` — the catalog was removed from the engine
- **`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs`** — `MaxOutputBytes = 50*1024` `:12`, `Truncate` `:23`, **`RedactSecrets` `:72-120` — ≈10 regexes, pattern-based only**; `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs:71` — `Clean(string?)`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ErrorRedactor.cs:30` — public API is exactly the ctor `:138` and `Redact(string)` `:144`; the 8-rule table `:121-132`; never throws `:173`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ToolCallValidator.cs` — `MaxArgumentSizeBytes = 100*1024` `:25`; the name regex `:28-29`; the recursive string sanitization `:158-160` / `SanitizeJsonStrings` `:184-233`
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ParallelToolExecutor.cs:121-122` — the linked-CTS `CancelAfter(toolTimeoutMs)` pattern; `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs:458-460` — the same on the sequential branch
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:464-474` (`ToolExecutionResult`), `:500` (`EnableParallelTools` defaults **false**)
- `apps/tamma-elsa/src/Tamma.Activities.Guardrails/Allowlist.cs:16-17`, `:45-64`, `:57-58`

## Corrections to the story

- **T1 — the two "deliberate divergences" are verified, and the second is sharper than the story states.**
  (i) `JiraBaseUrlGuard.ValidateAsync` **does** short-circuit on an allowlist hit and skip the private-range
  check, leaning on connect time alone (the branch around `:72-95`); this story must not copy that.
  (ii) The shipped guard is **stricter at validation than at connect**: `ValidateAsync` is all-or-nothing
  (`:113`, `addresses.Any(IsBlockedAddress)` → reject) while `SafeConnectAsync` **filters** (`:197`,
  `addresses.Where(a => !IsBlockedAddress(a))`) and connects to whatever survives, failing only when *every*
  address is blocked. So S4's second assertion — a host resolving to a mix of public and private addresses is
  rejected outright — is **new connect-time behaviour**, not a property of the helper being reused. A test
  written against the helper as-is would pass while the requirement is unmet. Implement it as an **opt-in
  strict mode** (`rejectOnAnyBlocked: true`) that this tool sets and the JIRA client does not.
- **T2 — S11.3's human-content-review affordance no longer exists, and S11's residual is therefore wider
  than the story admits.** S11.3 promised that a `destructive: true` binding would carry the redacted body
  into a `ToolAuthorizationRequest` an operator sees before the send. `ToolAuthorizationRequest` was 42-3's;
  42-3 is deleted; and Epic 43 deliberately names the Seam B outcome **`Denied`, not `RequiresHuman`**,
  because on the tool path *"there is no human… calling it escalation would be a lie."* **Consequence:
  Epic 43 can stop a call, but there is no mechanism by which an operator reviews its content and then
  releases it.** S11 keeps parts 1, 2 and 4 (the request cap bounds a single exfiltration; the digest + byte
  size make volume reconstructible; `ContentSanitizer.SanitizeInput` strips injected control payloads) and
  **loses part 3**. The story's own honest framing — *"the host allowlist constrains **who** receives data,
  not **what** is sent… no control in this story stops that, and the story must not claim otherwise"* —
  becomes more true, not less. Say so; do not quietly drop the clause.
- **T3 — `ToolOutputHelper.RedactSecrets` is pattern-based, which is exactly why S10 exists.** Verified at
  `:72-120`: ≈10 regexes over known credential shapes (`sk-`, `AKIA`, `gh[pousr]_`, `glpat-`, `xox[bp]-`,
  JWT, PEM, `Password=`). A random bound token matches **none** of them. S10's by-value scrubbing is not
  belt-and-braces; it is the only thing that works, and its test must use a token that provably defeats the
  pattern path.
- **T4 — `ErrorRedactor`'s public surface is two members.** The ctor (`:138`) and `Redact(string)` (`:144`);
  everything else is private static. Anything this story wants from it must go through `Redact`. It never
  throws (`:173`), returning `"[Error during redaction]"` on internal failure — so it is safe on the error
  path, which is where 42-9 needs it most.
- **T5 — `HttpRequestTool` is not on `TAMMA001`'s injection denylist and would not trip it.** Verified
  (closed 13-entry list at `Allowlist.cs:45-64`; the HTTP check fires only on a statically-resolvable literal
  external host, and this tool's host is always configuration-supplied). The story says so. Siting is settled
  by rule 1 and by the engine not hosting the catalog — **the analyzer is a backstop, not the enforcement.**

## Design Decisions

- **D1 — `HttpRequestTool`, the endpoint resolver and the guard wiring live in `Tamma.Api`**, package
  `Tamma.Api.Services.Tools.Http`, registered at `Program.cs:753-766`, with its **own named `HttpClient`**
  whose primary handler sets `AllowAutoRedirect = false` and `ConnectCallback = <the shared guard in strict
  mode>` — the `Program.cs:184-189` pattern. **Nothing is added to `Tamma.Activities`.** `ToolOutputHelper`,
  `ContentSanitizer` and `ErrorRedactor` are *consumed from* `Tamma.Activities` (no credential; safe to
  reference downward).
- **D2 — endpoint bindings move to deployment configuration, platform-scoped and startup-validated.** With
  42-2 deleted and Epic 43 storing policy only, this is the story's destination source:

  ```
  Http:Endpoints:<key>:BaseUri            — must be https
  Http:Endpoints:<key>:HostSuffixes:[]    — dot-boundary matched
  Http:Endpoints:<key>:Methods:[]         — the method allowlist
  Http:Endpoints:<key>:AuthMode           — header | bearer | basic
  Http:Endpoints:<key>:AuthHeaderName     — declared so it can be dropped structurally
  Http:Endpoints:<key>:HeaderNameAllowlist:[]
  Http:Endpoints:<key>:MaxRequestBytes / MaxResponseBytes
  Http:Endpoints:<key>:SecretName         — the logical name 42-4 resolves
  ```

  Bound via `IOptions<HttpEndpointOptions>` and **validated fail-loud at startup** (a non-https base URI, an
  empty host allowlist, or an undeclared auth header refuses to boot — the `PromptFileLoader` posture).
  An `endpointKey` absent from this set is a **refusal** — `Success = false`, zero sends — not a
  reclassification, because there is no class to assign. The `destructive: true` marker is **dropped**: it
  existed only to select a `PermissionClass` and to trigger S11.3's content review, and neither survives
  (T2). Governance is a catalog row on `tool:http_request`.
- **G1 — per-tenant endpoint bindings are a recorded capability gap, and it is the epic's most severe
  instance.** 42-2 would have let each `tenant_admin` bind their own endpoints and credentials, and the
  story's SaaS section promises exactly that: *"a test asserts an `endpointKey` bound by tenant A is not
  resolvable in a tenant-B run."* With D2's platform-scoped configuration, **that promise cannot be kept** —
  every tenant sees the same declared endpoint set. What *is* still enforced per tenant is the **credential**:
  42-4 resolves `SecretRef.ForTenant(runTenantId, "http/<key>")`, so tenant A's run authenticates with
  tenant A's secret and cannot reach tenant B's, even against the same endpoint. **The honest statement:
  destinations become platform-scoped; secrets stay tenant-scoped.** Single-user is fully served. **No
  per-tenant config store is invented here** (42-8 index C3 forbids it explicitly — the same gap hits 42-4,
  42-8A and 42-8B, and must be decided once at epic/Epic 43 level).
- **D3 — no model-supplied destination, enforced by the schema's shape, not by validation.** `InputSchema`
  contains no field accepting a scheme, host, authority or absolute URI (S1). The composed absolute URI is
  built server-side from the binding's base URI plus the model's relative `path`/`query`. An argument object
  carrying `url` / `baseUrl` / `origin` is rejected with zero sends. This is a schema-shape property, so it
  is asserted by inspecting the published schema *and* by the rejection test.
- **D4 — extract the SSRF guard, keep JIRA byte-identical, add strict mode as an opt-in.** Refactor
  `JiraBaseUrlGuard`'s `IsBlockedAddress` (`:135`) and `SafeConnectAsync` (`:186`) into a shared
  `Tamma.Api` helper. **The JIRA client keeps its current behaviour exactly** — including its allowlist
  short-circuit (T1(i)) and its connect-time *filtering* (T1(ii)) — and this tool opts into
  `rejectOnAnyBlocked: true` plus no-allowlist-short-circuit. Tightening JIRA in the same change is out of
  scope and would be an unreviewed behaviour change to a shipped integration.
- **D5 — the private-range floor runs at validation AND at connect, allowlisted or not.** An allowlist entry
  is a destination, not a bypass (S3). This is the first divergence from the shipped guard and the reason the
  extraction cannot be a pure move.
- **D6 — secret binding and the by-value scrub.**
  `SecretRequirement(SecretPurpose.ApiKey, "http/<endpoint-key>", Required)`, resolved by 42-4 to
  `SecretRef.ForTenant(runTenantId, name)` in SaaS and `SecretRef.ForPlatform(name)` in single-user (no user
  scope; `SecretScope` has exactly `Platform` and `Tenant`; `SecretRef`'s ctor throws on either mismatch).
  Applied at call time in the binding-declared auth mode. **S10's by-value scrubbing happens inside
  `ExecuteAsync`, which legitimately holds the plaintext, and before it returns** — body, echoed headers,
  status text, every error message — *in addition to* `ToolOutputHelper.RedactSecrets`. Downstream stays
  never-hold + pattern-only (42-4 D8). Structurally, the declared `AuthHeaderName` (D2) is **dropped from
  any echoed headers by name**, which catches the residual case value-matching cannot: an endpoint that
  transforms the credential (base64, a hash) before echoing it.
- **D7 — response and request handling.** Read under a hard byte cap enforced on the **stream**, not on a
  declared `Content-Length`; then `ToolOutputHelper.Truncate` (`:23`, 50 KB, which redacts first). Honour the
  per-tool timeout via the linked CTS (`InlineToolLoopRunner.cs:458-460` / `ParallelToolExecutor.cs:121-122`).
  A non-2xx — **including any 3xx, since redirects are not followed** — is a normal
  `ToolExecutionResult { Success = false }` with the redacted status and body, never a throw. Request bodies
  over the binding's cap are rejected **before** the send.
- **D8 — audit is 42-5's trio with HTTP tags.** `endpointKey`, `method`, composed **path** (never the query
  string verbatim), `statusCode`, request-body **byte size** and a request-body **content digest** (never the
  body). No new family. Any Epic 43 gate decision is recorded in Epic 43's event family.
- **D9 — one executor, not one per endpoint.** 42-1 D5 restricts dynamic `Register` to platform/deployment
  scope until 42-6 Part B's per-principal registry view, so minting a registered executor per endpoint
  binding is unavailable. One `http_request` executor plus D2's configuration is the shape — which,
  post-reconciliation, is also simpler than the story's, since there is no per-call class to compute.

## Implementation Steps

1. **Precondition gate.** 42-1, 42-4, 42-5 landed.
2. **CREATE `Tamma.Api/Services/Http/SsrfGuard.cs`** — extract `IsBlockedAddress` + `SafeConnectAsync` from
   `JiraBaseUrlGuard` (D4), adding `rejectOnAnyBlocked` and a no-short-circuit validation mode; **MODIFY
   `JiraBaseUrlGuard` + `Program.cs:184-189`** to delegate with its **current** semantics preserved exactly.
3. **CREATE `Tamma.Api/Services/Tools/Http/HttpEndpointOptions.cs`** — D2's shape with fail-loud startup
   validation.
4. **CREATE `.../Http/HttpEndpointResolver.cs`** — `endpointKey` → binding; refusal for anything undeclared.
5. **CREATE `.../Http/HttpRequestTool.cs`** — D3/D6/D7: schema with no destination field, URI composition,
   header/method/path containment, secret application, by-value scrub, caps, timeout, never-throw.
6. **MODIFY `Tamma.Api/Program.cs`** — the named `HttpClient` with `AllowAutoRedirect = false` +
   `ConnectCallback = SsrfGuard.StrictConnectAsync`, and the executor registration at `:753-766`.
7. **CREATE the test suites** (Test Plan) — S1–S11 are the bulk. Author the Epic 43 catalog entry for
   `tool:http_request` as **admin data**, not code.
8. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; confirm nothing
   was added to `Tamma.Activities` or `Tamma.ElsaServer`.

## Data & Migrations

None. Secrets are Epic 29's `secrets` table; events ride `IEventRepository` → `domain_events`; D2's bindings
are configuration.

## Events

Reuses 42-5's `TOOL.INVOKED`/`SUCCEEDED`/`FAILED` with `endpointKey` / `method` / composed-path /
`statusCode` / request-byte-size / body-digest tags — **never a query string verbatim, never a header value,
never the credential**. No new family, and no engine-side emission.

## Test Plan

Each S-criterion is a separate falsifiable test. "The allowlist contains it" is not a control.

- **`HttpToolDescriptorTests`** — registered; descriptor read through an **`IToolExecutor`-typed** reference
  declares `SecretRequirement(ApiKey, "http/<endpoint-key>", Required)` and `Suspends == false`.
  **Covers reconciled AC1.**
- **`HttpEndpointResolutionTests`** (D2) — a declared key resolves; an **unknown** key, a **missing** key and
  malformed JSON each yield `Success = false` with zero sends on a spy handler; startup refuses a non-https
  base URI, an empty host allowlist, and an undeclared auth header.
- **`HttpSsrfContainmentTests`** — the matrix: **S1** no destination field in `InputSchema`, and `url` /
  `baseUrl` / `origin` rejected with zero sends; **S2** https-only and dot-boundary host matching
  (`evilatlassian.net` must **not** match `.atlassian.net`); **S3** table-driven per range — `127.0.0.1`,
  `::1`, `0.0.0.0`, `10.x`, `172.16–31.x`, `192.168.x`, `169.254.169.254`, `fe80::/10`, `fc00::/7` and an
  IPv4-mapped IPv6 form of each — rejected as a literal host **and** via DNS, **including when the host is
  allowlisted** (the T1(i) divergence, pinned); **S4** resolve-then-pin: a resolver returning public at
  validation and private at connect fails with no bytes sent; a mixed public/private host is **rejected
  outright** (T1(ii) — new behaviour, and a third test pins that the **JIRA client keeps filtering**);
  **S5** a stub returning `302 Location: http://169.254.169.254/…` yields `Success = false` with the 3xx
  status, the spy records exactly **one** send, and the `Location` is redacted out of the body.
- **`HttpCompositionContainmentTests`** — **S6** `..` segments, a leading `/`, an absolute URI, a
  scheme-relative `//evil.example`, userinfo (`user@host`), backslashes, control characters, CR/LF, and any
  percent- or double-encoded form decoding to one of those are rejected; for every input the **composed
  absolute URI's** scheme, host, port and base-path prefix are byte-identical to the binding's. **S7**
  headers limited to the per-binding name allowlist; `Host`, `Authorization`, `Proxy-*`, `Cookie`, `Cookie2`
  and the auth-mode header refused unconditionally; values containing CR, LF or NUL rejected, not sanitized;
  a `Host` override and a second `Authorization` both fail with zero sends. **S8** a method outside the
  allowlist is rejected with zero sends, and a GET/HEAD-only binding rejects POST.
- **`HttpCapsAndTimeoutTests`** — **S9** an over-cap request body is rejected **before** the send; a stub
  declaring `Content-Length: 10` and streaming 10 MB is cut at the cap (the cap is on the **stream**), and
  the result carries `ToolOutputHelper.Truncate`'s `[truncated: …]` marker; the per-tool timeout cancels an
  unresponsive endpoint via the linked CTS. **Covers AC5 (timeout half).**
- **`HttpCredentialNonLeakTests`** — **S10**, and the test is deliberately hostile: a **random 40-char
  token** matching **no** `RedactSecrets` pattern (T3), reflected by the stub in **both** the body and a
  response header; grep for the literal across `ToolExecutionResult.Output`, every emitted `TOOL.*` payload
  and all captured log lines; plus the structural case — a credential the endpoint **transforms** (base64)
  before echoing is caught by the declared-auth-header drop (D6), not by value matching. **Covers AC4.**
- **`HttpExfiltrationBoundsTests`** — **S11** as reconciled: (1) the request cap bounds a single
  exfiltration; (2) `TOOL.INVOKED` carries `endpointKey`, method, composed path, request-body byte size and a
  content **digest**, and **not** the body; (4) arguments pass through `ContentSanitizer.SanitizeInput`
  before composition. **Part (3) is not tested because it no longer exists (T2)** — a comment in the suite
  says so and names the residual, so a future reader does not assume coverage.
- **`HttpNeverThrowTests`** — non-2xx, 3xx, timeout and transport exception each yield `Success = false` with
  redacted status/body, and `ExecuteAsync` **returns** rather than propagating. Asserted on **both** branches
  (`EnableParallelTools` `false` — the default — and `true`). **Covers AC5.**
- **`HttpTenantCredentialIsolationTests`** (G1) — SaaS resolves `SecretRef.ForTenant(runTenantId, name)`,
  single-user `SecretRef.ForPlatform(name)`; a `Tenant`-scoped ref with a null tenant id throws; and — the
  honest replacement for the story's tenant-A/tenant-B endpoint test — a tenant-B run against the same
  platform-declared `endpointKey` authenticates with **tenant B's** secret and cannot obtain tenant A's.
  **Covers reconciled AC4; G1 is asserted, not assumed.**

## Definition of Done

| AC (reconciled) | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — registered; secret requirement; `Suspends = false` | 5, 6 | `HttpToolDescriptorTests` |
| 4 — credential scoping (tenant-scoped secrets, platform-scoped destinations) | 5 (D6/G1) | `HttpTenantCredentialIsolationTests` |
| 5 — non-2xx / timeout / transport all `Success = false`, never a throw, both branches | 5 (D7) | `HttpNeverThrowTests`, `HttpCapsAndTimeoutTests` |
| 6 — `TOOL.*` tags carry no query string, header value or credential | 5 (D8) | `HttpCredentialNonLeakTests`, `HttpExfiltrationBoundsTests` |
| S1–S3, S5 — no model destination; scheme/host allowlist; unconditional address floor; no redirects | 2, 5, 6 (D3/D5) | `HttpSsrfContainmentTests` |
| S4 — resolve-then-pin, strict mode, JIRA unchanged | 2 (D4/T1) | `HttpSsrfContainmentTests` (all three cases) |
| S6–S8 — path/query, header and method containment | 5 (D3) | `HttpCompositionContainmentTests` |
| S9 — caps both directions, stream-enforced | 5 (D7) | `HttpCapsAndTimeoutTests` |
| S10 — the credential never leaves | 5 (D6) | `HttpCredentialNonLeakTests` |
| S11 (1,2,4) — bounded and auditable, not prevented | 5 (D8) | `HttpExfiltrationBoundsTests` |
| ~~2 (`Describe`), 3 (`ToolAuthorizationRequired`), 6's decision id~~ | — | **STRIPPED — Epic 43 governs** |
| ~~S11.3 — operator reviews the redacted body before the send~~ | — | **DELETED — no such mechanism exists post-reconciliation (T2); the residual is wider and is stated** |

## Blocks / Blocked by

- **Blocked by — 42-1, 42-4, 42-5.** All hard, all Wave 1.
- **Blocked by — Epic 43 for governance, not for shipping.** The containment matrix is the safety story here
  and it holds with no catalog row: the tool physically cannot reach an undeclared host, follow a redirect,
  or be handed a URL. What is absent until Epic 43 Story 9 is any *policy* gate on whether the agent may call
  it at a given autonomy — and, per T2, there is no content-review path even then.
- **Open product question — G1 / 42-8 index C3.** Per-principal tool configuration has no owner. Here it is
  most severe: it converts a promised per-tenant endpoint binding into a platform-scoped one. **Do not solve
  it inside this story.**
- **Wave position is now uncertain.** The story claims *"ship first among the families… for the least
  surface."* The second half of that is no longer true: D2 makes this story own an entire configuration
  surface, and S1–S11 was always the bulk. Per 42-8 index C2, **42-8A should ship first if this story slips**
  — it is independent, has no engine-side work, and delivers 41-22's kill-switch.
- **Blocks — the most Epic 41 consumers of any family:** **41-5** (stakeholder update → Slack/Jira/webhook),
  **41-7** (standup digest publish), **41-24 / 41-25 / 41-26** (docs, release-notes and runbook publish, HTTP
  flavour — the git flavour already exists via `GitOperationsTool`), and any future integration workflow.

## Risks & Mitigations

- **Extracting the guard touches a shipped path.** Mitigation: D4 keeps JIRA byte-identical — including its
  allowlist short-circuit and its connect-time filtering — and adds strict mode as an opt-in; a dedicated
  test pins that JIRA still filters (S4's third case). Tightening JIRA is explicitly out of scope.
- **A test written against the helper as-is would pass while S4 is unmet (T1(ii)).** This is the subtlest
  trap in the story: `SafeConnectAsync` filters where `ValidateAsync` rejects. Mitigation: T1 states it, and
  S4's mixed-address case is written against the **new strict mode**, not the shipped helper.
- **Pattern redaction cannot protect a bound token (T3).** Mitigation: S10's by-value scrub inside the
  executor, plus the declared-auth-header structural drop for the transformed-credential residual, plus a
  test that deliberately uses a pattern-defeating token.
- **Exfiltration to an allowlisted destination is unsolved, and now less mitigated (T2).** The host allowlist
  constrains **who** receives data, not **what** is sent, and S11.3's content-review affordance is gone.
  Mitigation: bounded by the request cap and auditable by digest + byte size; **stated as a residual, not as
  a control**. Anyone reading this story must not treat the host allowlist as an anti-exfiltration measure.
- **Configuration sprawl (D2).** Ten keys per endpoint, validated only at startup. Mitigation: fail-loud
  validation refuses to boot on a bad binding, which is strictly better than a per-call surprise; and the
  same shape is now used by 42-8A and 42-8B, so it is a house pattern rather than a one-off.
- **Siting drift.** Mitigation: D1's rule, with the honest note (T5) that `TAMMA001` would not mechanically
  catch it — this tool's host is always dynamic, exactly the case the analyzer's HTTP check cannot see.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1–2 | Precondition gate + SSRF guard extraction, strict mode, JIRA-unchanged refactor | 1.0 |
| 3–4 | `HttpEndpointOptions` with fail-loud validation + the resolver | 0.75 |
| 5 | `HttpRequestTool` (composition, containment, auth modes, by-value scrub, caps, timeout) | 1.25 |
| 6 | Named `HttpClient` + handler wiring + registration | 0.25 |
| 7 | The S1–S11 matrix + the other suites (the bulk of the story) | 2.0 |
| 8 | Full green + catalog-row authoring notes | 0.25 |
| **Total** | | **5.5** |

Story estimate: ~4–5 days (itself corrected upward from ~3–4). The reconciliation **added** roughly half a
day net: stripping `Describe` and the authorization tests saved less than D2's configuration surface and its
startup validation cost, because the endpoint bindings still have to exist somewhere and this story now owns
them outright. This is the family the reconciliation made *harder*, not easier.
