# Story 42-9: Authenticated HTTP / External-API Tool

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running a workflow that must reach an external service**, I want a **generic authenticated
HTTP tool** that makes one REST call to a **host-and-method-allowlisted, credential-bound** endpoint, so
that a stakeholder update, a standup digest, or a docs publish can actually be posted — without opening a
raw egress hole or leaking the credential.

## Priority

P2 / Wave 3 — **ship first among the families.** It unblocks the most Epic 41 workflows (41-5, 41-7,
41-24/25/26 all need "post this artifact somewhere") for the least surface, and it is the safe,
general-purpose capability the platform is missing most.

## The gap (READ FIRST)

An agent that produces a stakeholder update (41-5) or a standup digest (41-7) has **no way to deliver
it** — the six built-ins can write a file, commit, and shell, but there is no governed way to POST to
Slack/Jira/a webhook. `ShellExecute` + `curl` is the current de-facto path — unbound, unaudited, and
egress-unbounded (and `ActionGate` even blocks common `curl | bash` patterns). This story gives a
first-class, allowlisted, credential-bound HTTP capability.

## Scope

1. **`HttpRequestTool : IToolExecutor`.** One call = one request to a **bound endpoint**. The tool does
   **not** accept an arbitrary URL from the model; it accepts an **endpoint key** (resolved via the 42-2
   binding's `config` to a base host + a **host+method allowlist**) plus a path/body/headers the model
   fills. A request to a host or method not in the binding's allowlist is rejected loudly (SSRF / egress
   containment). This keeps the model from being tricked into hitting arbitrary or internal hosts
   (defense against prompt-injected exfiltration — consistent with `ContentSanitizer`/`ActionGate`
   posture).

2. **Permission class.** `ReadOnly` for a GET-only binding; `Mutating` for a binding that permits
   POST/PUT/PATCH/DELETE (the default). Rarely `Destructive` — a specific binding an operator marks so
   (e.g. an endpoint that tears something down) inherits destructive routing via 42-3. Class comes from
   the **binding**, not the model's method choice, so it can't be downgraded by the caller.

3. **Secret binding.** `SecretRequirement(SecretPurpose.ApiKey, "http/<endpoint-key>", Required)` via
   42-4 — injected as the configured auth (header/bearer/basic per binding `config`), tenant-scoped in
   SaaS / user-scoped in single-user. The credential is applied at call time and **never** echoed into
   `Output` (a response body that reflects the token is redacted before the result leaves `ExecuteAsync`)
   or into 42-5 events.

4. **Response handling.** Truncate large responses (`ToolOutputHelper.Truncate`), cap body size, honor
   the per-tool timeout (the `ParallelToolExecutor` linked-CTS pattern). A non-2xx is a normal
   `ToolExecutionResult` (`Success = false` with the redacted status/body) — not a throw.

5. **Docs-publish note.** For 41-24/25/26 "publish", the delivery is often either a git push (existing
   `GitOperationsTool` to the wiki repo) **or** an HTTP POST to a docs host — this tool covers the HTTP
   flavor; the git flavor already exists. The workflow declares whichever it needs (42-3 resolution).

## Acceptance Criteria

1. The tool rejects a request to a host/method outside the binding's allowlist, loudly (test — SSRF/egress
   containment); an in-allowlist request succeeds against a stub server.
2. The endpoint key resolves to host+auth via the 42-2 binding; the model cannot supply a raw arbitrary
   URL (test asserts an off-allowlist or raw-URL attempt is rejected).
3. Permission class comes from the binding, not the request method — a `ReadOnly` (GET-only) binding
   rejects a POST attempt (test); it can't be escalated by the caller.
4. Auth credential binds tenant/user-scoped (42-4), is applied at call time, and never appears in
   `Output` or any 42-5 event, even when the response reflects it (grep-for-value test).
5. Non-2xx yields `Success = false` with redacted status/body; timeout yields a `TOOL.FAILED`; neither
   throws.
6. A destructive-marked binding routes through 42-3 authorization (test).

## Events

Reuses 42-5 `TOOL.*` with `endpointKey`/`method`/`statusCode` tags (never URL query secrets, never auth).
No new family.

## Single-user vs SaaS

- **single-user:** the user's endpoint bindings + credentials; the user owns the host allowlist.
- **SaaS:** tenant-scoped bindings + credentials; a tenant can reach only the endpoints its
  `tenant_admin` bound, with the tenant's own credential — a tenant's egress and secrets never cross the
  boundary.

## Epic 41 consumers

**41-5** (stakeholder / status update → post to Slack/Jira/email webhook), **41-7** (standup digest
publish), **41-24/25/26** (docs / release-notes / runbook publish, HTTP flavor), and any future
integration workflow — the most broadly-consumed family in the epic.

## Dependencies

- **42-1** (descriptor), **42-2** (endpoint-key binding + host/method allowlist in `config`), **42-3**
  (gating; destructive-binding authorization), **42-4** (auth secret — hard-blocked on the Epic 29
  reveal path), **42-5** (audit + redaction).
- **Existing:** `ToolOutputHelper.Truncate`, the `ParallelToolExecutor` timeout pattern,
  `ContentSanitizer`/`ErrorRedactor`.
- **Epic 41** 41-5/41-7/41-24/25/26 consumers.

## Risks

- **SSRF / egress / exfiltration.** A generic HTTP tool is the classic prompt-injection exfiltration
  vector. Mitigation: **no raw URL from the model** — endpoint-key + host/method allowlist from the
  binding, class from the binding not the method, and the same sanitize/redact envelope. This is the
  central design constraint, not an afterthought.
- **Response-reflected secrets.** An endpoint that echoes the auth token in its body. Mitigation: the
  grep-for-value redactor at the `ExecuteAsync`/emit boundary (AC4).

## Estimated Effort

Medium. ~3–4 days (single tool, but the allowlist/SSRF containment + redaction carry the weight).
</content>
