# Story 42-9: Authenticated HTTP / External-API Tool

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running a workflow that must reach an external service**, I want a **generic
authenticated HTTP tool** that makes one REST call to a **binding-resolved, host-and-method-allowlisted,
credential-bound** endpoint, so that a stakeholder update, a standup digest, or a docs publish can
actually be posted — without opening a raw egress hole or leaking the credential.

## Priority

P2 / Wave 3 — **ship first among the families.** It unblocks the most Epic 41 workflows (41-5, 41-7,
41-24/25/26 all need "post this artifact somewhere") for the least surface, and it has no engine-side
work at all.

## The gap (READ FIRST)

An agent that produces a stakeholder update (41-5) or a standup digest (41-7) has **no way to deliver
it** — the six built-ins can write a file, commit, and shell, but there is no governed way to POST to
Slack/Jira/a webhook. `ShellExecute` + `curl` is the current de-facto path — unbound, unaudited, and
egress-unbounded (`ActionGate` blocks common `curl | bash` shapes, not egress).

**The SSRF controls this story needs already exist in-repo and must be reused, not re-derived.**
`Tamma.Api/Services/Integrations/JiraBaseUrlGuard` ships `IsBlockedAddress` (loopback, `0.0.0.0/8`,
`10/8`, `127/8`, `169.254/16` incl. cloud metadata, `172.16/12`, `192.168/16`, IPv6 `::`, `fe80::/10`,
`fc00::/7`, with IPv4-mapped-IPv6 unwrapping), a dot-boundary host-suffix allowlist matcher, and
`SafeConnectAsync` — a `SocketsHttpHandler.ConnectCallback` that re-resolves at **connect** time to
close the DNS-rebinding TOCTOU window. It is already wired that way at `Tamma.Api/Program.cs` L185–188
together with `AllowAutoRedirect = false`.

**Two deliberate divergences — both must be built, neither is inherited.**

1. **No allowlist short-circuit.** `JiraBaseUrlGuard.ValidateAsync` **short-circuits on an allowlist
   hit** (L73–81) and skips the private-range check, leaning on connect time alone. This story does
   **not** copy that: the private/loopback/link-local/metadata floor is enforced at validation **and**
   at connect, allowlisted or not. An allowlist entry is a destination, not a bypass.
2. **Connect-time is all-or-nothing, not filter-and-connect.** `SafeConnectAsync` today *filters*:
   `addresses.Where(a => !IsBlockedAddress(a))` and connects to whatever survives, failing only when
   **every** address is blocked. `ValidateAsync` is already all-or-nothing (`addresses.Any(IsBlockedAddress)`
   → reject), so the shipped guard is stricter at validation than at connect. S4's second assertion —
   a host resolving to a mix of public and private addresses is rejected outright — is therefore
   **new behaviour at connect time**, not a property of the helper being reused. Implement it as an
   opt-in strict mode on the extracted guard (`rejectOnAnyBlocked: true`) that this tool sets and the
   JIRA client does not, per the "keep JIRA byte-identical" risk below.

## Where this code lives (binding)

**`HttpRequestTool`, the endpoint resolver, and the guard wiring live in `Tamma.Api`** — package
`Tamma.Api.Services.Tools.Http`, registered next to the six built-ins at `Tamma.Api/Program.cs`
L753–766, with its own named `HttpClient` whose primary handler sets `AllowAutoRedirect = false` and
`ConnectCallback = <the shared SSRF guard>`. Nothing here is added to `Tamma.Activities`.

Reasons, in force order: (1) **rule 1** — a workflow step never calls an external API or holds an
external credential; this tool is nothing but that; (2) **runtime** — `Tamma.ElsaServer/Program.cs`
L286–292 records the tool catalog was *removed* from the engine and "the tool executors are registered
there [`Tamma.Api`], not here", so an engine-side executor is never resolved; (3) **guardrail
backstop** — `TAMMA001` (`DiagnosticSeverity.Error`, analyzer-referenced by `Tamma.Activities` /
`Tamma.ElsaServer`) exists precisely to stop credentialed vendor calls re-entering the engine, and
`Allowlist.IsEngineSurface` deliberately excludes `Tamma.Api`. *Honest scope:* `TAMMA001`'s HTTP check
fires only on an `HttpClient` send whose host is a **statically-resolvable literal external host** —
this tool's host is always dynamic, so it would not mechanically trip. Siting is settled by (1) and
(2); the analyzer is the backstop. Precedent: `GetAcceptanceRulesTool` in
`Tamma.Api.Services.AcceptanceRules`; `Allowlist.cs` L57–58 on `InlineToolLoopRunner`.

Only the 42-1 contract types stay in `Tamma.Activities.LlmCall.Tools`; `ToolOutputHelper`,
`ContentSanitizer` and `ErrorRedactor` are consumed from `Tamma.Activities` (no credential, safe to
reference downward).

## Scope

1. **`HttpRequestTool : IToolExecutor` — one call, one request, no URL from the model.** The tool takes
   an **`endpointKey`** (resolved through the 42-2 binding's `ConfigJson` to a base URI + host allowlist
   + method allowlist + auth mode + caps) plus a relative `path`, `query`, `body`, and a restricted set
   of `headers`. There is **no URL parameter in `InputSchema`**. The composed absolute URI is built
   server-side from the binding; the model contributes only what §S6–S8 permit.

2. **Permission class — DECIDED: from the binding, reported per call.**
   *Corrected: an earlier draft asserted "class comes from the binding, not the method" but left the
   descriptor a single fixed value, which cannot express many bindings of different classes behind one
   executor.* Because 42-1 §3 restricts dynamic `Register` to **platform/deployment scope only** until
   42-6's per-principal registry view lands, minting one registered executor per principal-scoped
   endpoint binding is **not available in Wave 3**. So: one executor, and 42-3's per-call seam does the
   work — `ToolInvocationFacts Describe(string argumentsJson)` resolves `endpointKey` → binding and
   returns `{ PermissionClass, Operation = "<method> <endpointKey>", Target = "<endpointKey>:<path>" }`:

   | Binding | Per-call `PermissionClass` |
   |---|---|
   | `methods: [GET, HEAD]` only | `ReadOnly` |
   | any binding permitting POST/PUT/PATCH/DELETE (the default) | `Mutating` |
   | a binding an operator marks `destructive: true` | `Destructive` |

   An `endpointKey` the principal has no binding for, or a malformed argument object, returns the
   fail-safe facts (`Destructive`, `Operation = "http_request"`, `Target = null`) — deny-by-default. The
   class can never be raised or lowered by the model's method choice.

3. **Secret binding.** `SecretRequirement(SecretPurpose.ApiKey, "http/<endpoint-key>", Required)` —
   `SecretPurpose` being the `Tamma.Core`-sited enum 42-1 §0 relocates. Resolved by 42-4 to
   **`SecretRef.ForTenant(runTenantId, name)` in SaaS** and **`SecretRef.ForPlatform(name)` in
   single-user**. *Corrected: an earlier draft said "user-scoped in single-user" — there is no user
   scope; `SecretScope` has exactly `Platform` and `Tenant`, `SecretRef`'s constructor throws on either
   mismatch, and the sole user's ownership is `SecretMetadata.OwnerUserId` metadata.* The credential is
   applied at call time in the binding-declared auth mode (header / bearer / basic) and never echoed
   anywhere (§S10).

4. **Response handling.** Read under a hard byte cap, then `ToolOutputHelper.Truncate`
   (`MaxOutputBytes` = 50 KB) — the cap is enforced on the **stream**, not on a `Content-Length` the
   server declares. Honor the per-tool timeout via the `ParallelToolExecutor` linked-CTS
   (`CancelAfter(toolTimeoutMs)`) pattern. A non-2xx (including a 3xx, since redirects are not followed)
   is a normal `ToolExecutionResult` with `Success = false` and the redacted status/body — never a throw.

5. **Docs-publish note.** For 41-24/25/26 "publish", delivery is often either a git push (existing
   `GitOperationsTool` to the wiki repo) **or** an HTTP POST to a docs host — this tool covers the HTTP
   flavor; the git flavor already exists. The workflow declares whichever it needs.

## Acceptance Criteria

1. `HttpRequestTool` is registered in `Tamma.Api`; its descriptor, read through an **`IToolExecutor`-typed**
   reference (42-1's DIM caveat), is `Destructive` (family max) / floor 100 / `Suspends = false`, with
   `SecretRequirement(ApiKey, "http/<endpoint-key>", Required)`.
2. `Describe(argumentsJson)` is table-driven-tested against the §2 table: a GET/HEAD-only binding →
   `ReadOnly`; a POST-permitting binding → `Mutating`; a `destructive: true` binding → `Destructive`;
   and each of {unknown `endpointKey`, missing `endpointKey`, malformed JSON} → `Destructive` with
   `Target = null`. `Operation` equals `"<method> <endpointKey>"`.
3. A `destructive: true` binding routes through 42-3 stage-2 authorization before the request is sent —
   **zero** `HttpClient` sends on a spy handler — and the emitted `ToolAuthorizationRequest` carries the
   operation and `Target`. Asserted on **both** execution branches, `EnableParallelTools = false` (the
   default) and `true`.
4. Credential scoping: SaaS resolves `SecretRef.ForTenant(runTenantId, name)`, single-user resolves
   `SecretRef.ForPlatform(name)`; constructing a `Tenant`-scoped ref with a null tenant id throws.
5. Non-2xx yields `Success = false` with redacted status/body; a per-tool timeout yields
   `Success = false` + `TOOL.FAILED`; a transport exception yields `Success = false` — the test asserts
   `ExecuteAsync` **returns** rather than propagating (never-throw contract).
6. `TOOL.*` rows carry `endpointKey` / `method` / composed-path / `statusCode` / request-body byte size
   and never the URL query string verbatim, never a header value, never the credential.

### Acceptance criteria — SSRF / egress containment

*These are the story. Each is a separate falsifiable test; "the allowlist contains it" is not a control
by itself.*

- **S1 — No model-supplied destination.** The published `InputSchema` contains no field accepting a
  scheme, host, authority, or absolute URI. An argument object carrying `url` / `baseUrl` / `origin` is
  rejected with `Success = false` and **zero** sends on a spy handler.
- **S2 — Scheme + host allowlist.** The binding's base URI must be `https`; the resolved host must match
  a binding host-suffix entry by **exact or dot-boundary** match (`evilatlassian.net` must **not** match
  `.atlassian.net`). A host outside the allowlist is rejected loudly, before any DNS or socket work.
- **S3 — Address denial, unconditional.** Table-driven per range: `127.0.0.1`, `::1`, `0.0.0.0`,
  `10.x`, `172.16–31.x`, `192.168.x`, `169.254.169.254` (cloud metadata), `fe80::/10`, `fc00::/7`, and
  an IPv4-mapped IPv6 form of each — rejected whether supplied as a literal host or reached by DNS, and
  **including when the host is allowlisted**. A test pins the divergence from
  `JiraBaseUrlGuard.ValidateAsync`'s allowlist short-circuit (L73–81): the floor is not skipped here.
- **S4 — Resolve-then-pin (DNS rebinding).** The socket connects only to an address re-checked at
  connect time via the shared `ConnectCallback`. A test with a resolver returning a public address at
  validation and a private one at connect asserts the connection **fails** and no bytes are sent. A
  second test asserts a host resolving to a mix of public and private addresses is rejected outright
  (partial-private is treated as hostile), not silently connected to the public one — this is
  divergence 2 above and is **new** connect-time behaviour: the shipped `SafeConnectAsync` filters the
  blocked addresses out and connects to the survivors, so a test written against the helper as-is
  would pass while the requirement is unmet. A third test pins that the JIRA client keeps the
  filtering behaviour (the strict mode is opt-in, not a global tightening).
- **S5 — Redirects are never followed.** `AllowAutoRedirect = false` (the `Program.cs` L185–188
  pattern). A stub returning `302 Location: http://169.254.169.254/…` yields `Success = false` with the
  3xx status, and the spy handler records exactly **one** send — the `Location` is never fetched, and it
  is redacted out of the returned body.
- **S6 — Path/query containment.** The model's `path`/`query` are appended to the binding's base path
  and may not escape it. Rejected: `..` segments, a leading `/` that would replace the base path, an
  absolute URI, a scheme-relative `//evil.example`, userinfo (`user@host`), backslashes, control
  characters and CR/LF, and any percent- or double-encoded form that decodes to one of those. The test
  asserts the **composed absolute URI's** scheme, host, port and base-path prefix are byte-identical to
  the binding's for every input in the table.
- **S7 — Header restrictions.** Model-supplied headers are limited to a per-binding **name allowlist**.
  Unconditionally refused regardless of allowlist: `Host`, `Authorization`, `Proxy-*`, `Cookie`,
  `Cookie2`, and any header name the binding's auth mode sets. Header **values** containing CR, LF, or
  NUL are rejected, not sanitized. A test asserts a model attempt to override `Host` (virtual-host
  routing to an internal service) and to inject a second `Authorization` both fail with zero sends.
- **S8 — Method restriction.** Only methods in the binding's method allowlist are sent; a method outside
  it is rejected with zero sends. A `ReadOnly` (GET/HEAD-only) binding rejects a POST attempt, and the
  permission class is unchanged by the attempt (S8 and AC2 are asserted together so a caller cannot
  escalate by choosing a method).
- **S9 — Size and time caps, both directions.** The request body is capped by the binding
  (default small) and an over-cap body is rejected **before** the send. The response is read under a
  hard byte cap enforced on the stream: a stub that declares `Content-Length: 10` and then streams 10 MB
  is cut at the cap, and the result is truncated by `ToolOutputHelper.Truncate` with its
  `[truncated: …]` marker. The per-tool timeout cancels an unresponsive endpoint via the linked CTS.
- **S10 — The credential never leaves.** Redaction is **by value** — the resolved secret string is
  replaced in the body, headers-echo, status text, and any error message at the `ExecuteAsync`
  boundary, *in addition to* `ToolOutputHelper.RedactSecrets`. *This does **not** contradict 42-4 §3 /
  42-5 §3, which retire value-matching at the **audit** boundary: by-value scrubbing happens **here**,
  inside the executor, which legitimately holds the plaintext because it just authenticated with it,
  and it happens **before** `ExecuteAsync` returns. Nothing downstream — the 42-5 emitter, the DCB
  row, the `ToolAuthorizationRequest`, the log — is ever handed the value, so downstream stays
  never-hold + pattern-only. The rule is "by-value at the `ExecuteAsync` boundary, never-hold after
  it"; an implementer must not "simplify" by passing the credential outward to redact later.* This is
  load-bearing:
  `RedactSecrets` is **pattern-based** (`sk-…`, `AKIA…`, `gh[pousr]_…`, `glpat-…`, `xox[bp]-…`, JWT,
  PEM, `Password=`) and matches **no** arbitrary bound token. The test uses a random 40-char token, has
  the stub reflect it in the body **and** in a response header, and greps for that literal value across
  `ToolExecutionResult.Output`, every emitted `TOOL.*` payload, the `ToolAuthorizationRequest`, and all
  captured log lines.
- **S11 — Exfiltration to an *allowlisted* destination is bounded and auditable, not prevented.**
  *Stated honestly: the host allowlist constrains **who** receives data, not **what** is sent. A
  prompt-injected agent can still POST repository content to a legitimately bound Slack webhook. No
  control in this story stops that, and the story must not claim otherwise.* What is asserted:
  1. the request-body cap (S9) bounds a single exfiltration to the binding's configured size;
  2. every call emits `TOOL.INVOKED` carrying `endpointKey`, method, composed path, request-body **byte
     size** and a **content digest** (never the body), so an anomalous volume/pattern is reconstructible
     after the fact — a test asserts the digest and size are present and the body is not;
  3. a binding may set `destructive: true` purely to force human review of its *content*: the
     `ToolAuthorizationRequest` then carries the redacted body, and a test asserts an operator sees it
     before the send;
  4. tool arguments pass through `ContentSanitizer.SanitizeInput` before composition, so injected
     control/bidi/zero-width payloads are stripped.
  Residual, recorded not solved: a single in-cap POST to an allowlisted destination is indistinguishable
  from legitimate use at call time.

## Events

Reuses 42-5 `TOOL.*` with `endpointKey` / `method` / composed-path / `statusCode` / request-byte-size /
body-digest tags (never a query string verbatim, never a header value, never auth). No new family, and
no engine-side emission — everything here runs in `Tamma.Api`, which appends directly to
`IEventRepository`.

## Single-user vs SaaS

- **single-user:** the sole user's endpoint bindings; the credential is a **platform-scoped** secret
  (`SecretRef.ForPlatform`) owned via `SecretMetadata.OwnerUserId`, and the user owns the host allowlist.
- **SaaS:** tenant-scoped bindings and credentials — a tenant can reach only the endpoints its
  `tenant_admin` bound, with the tenant's own credential. A test asserts an `endpointKey` bound by
  tenant A is not resolvable in a tenant-B run (it returns the `Describe` fail-safe and is rejected),
  so egress and secrets never cross the boundary.

## Epic 41 consumers

**41-5** (stakeholder / status update → post to Slack/Jira/email webhook), **41-7** (standup digest
publish), **41-24/25/26** (docs / release-notes / runbook publish, HTTP flavor), and any future
integration workflow — the most broadly-consumed family in the epic.

## Dependencies

- **42-1** — `ToolDescriptor`; the `Tamma.Core`-sited `SecretPurpose` (§0); and §3's restriction of
  dynamic `Register` to platform scope, which is **why** this is one executor plus `Describe` rather
  than one registered tool per endpoint binding.
- **42-2** — the binding's `ConfigJson` carries base URI, host-suffix allowlist, method allowlist, auth
  mode, header-name allowlist, request/response caps and the `destructive` marker. Without it this tool
  has no destination and no class.
- **42-3** — `Describe` + stage-2 argument-bound authorization on both branches, and the
  `ToolAuthorizationRequired` code.
- **42-4** — auth secret. *Corrected: an earlier draft called this "hard-blocked on the Epic 29 reveal
  path". It is not — four runtime plaintext readers already ship and 42-4 generalizes them. The residual
  dependency is a non-null `ISecretAccessAuditor`; only `NullSecretAccessAuditor` is registered today.*
- **42-5** — `TOOL.*` audit + redaction.
- **Existing, reused not rebuilt:** `JiraBaseUrlGuard.IsBlockedAddress` / `SafeConnectAsync` and the
  `AllowAutoRedirect = false` handler wiring (`Program.cs` L185–188) — extract the guard to a shared
  `Tamma.Api` SSRF helper rather than copying it; `ToolOutputHelper.Truncate` (50 KB) and
  `RedactSecrets` (pattern-based — see S10); the `ParallelToolExecutor` linked-CTS timeout;
  `ContentSanitizer` / `ErrorRedactor`.
- **`Tamma.Activities` holds no external credential** and carries the `TAMMA001` analyzer; no code from
  this story is added to it. This story adds **nothing** engine-side.
- **Epic 41** 41-5 / 41-7 / 41-24/25/26 consumers.

## Risks

- **Stage-1 filter vs. max-class descriptor — settled in 42-3, and the hardest dependency here.**
  This executor's max **is** `Destructive` (any binding may be marked so), and dynamic per-binding
  registration is unavailable until 42-6, so there is exactly one registered `http_request` carrying
  the max of *all* its bindings. A stage-1 filter reading the raw descriptor max would leave the whole
  family unreachable. 42-3 Scope 1 now keys stage 1 on the **binding-resolved effective ceiling for
  the principal**, with `Destructive` as a stage-2 discriminator (42-3 AC1b). This story is the most
  exposed to that decision: with one executor and many bindings, the ceiling must be computed as the
  max over the principal's *bindings*, and a principal holding only GET/HEAD bindings must resolve to
  a `ReadOnly` ceiling — add that case to 42-3's AC1b table.
- **Extracting the guard touches a shipped path.** Refactoring `JiraBaseUrlGuard` into a shared helper
  changes the JIRA client's wiring. Keep the JIRA behaviour byte-identical (including its allowlist
  short-circuit) and add the stricter mode as an opt-in this tool sets, rather than tightening JIRA in
  the same change.
- **Response-reflected and log-reflected secrets.** Covered by S10's by-value redaction; the residual is
  a credential the endpoint transforms (base64, a hash) before echoing it, which value-matching cannot
  catch. Mitigation: the binding declares the auth header/field name so it is dropped structurally, not
  only matched.
- **Exfiltration through an allowlisted destination is unsolved** (S11). Bounded by the request cap and
  auditable via digest + size; not prevented. Anyone reading this story should not treat the host
  allowlist as an anti-exfiltration control.

## Estimated Effort

Medium–Large. ~4–5 days — the tool itself is small; the S1–S11 containment matrix, the guard extraction,
by-value redaction and the binding-driven class resolution carry the weight. *Corrected upward from
~3–4 days: the earlier estimate predated the enumerated SSRF criteria and the `Describe` seam.*
