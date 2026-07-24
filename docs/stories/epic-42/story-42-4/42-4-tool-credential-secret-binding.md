# Story 42-4: Tool Credential / Secret Binding

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **tool that touches an external system**, I want to **bind to a stored secret** resolved for my
principal — per-tenant in SaaS, per-user in single-user — and receive the live credential at execution
time **without it ever reaching a log, an event, or the tool's output**, so that a cloud/flag/deploy/HTTP
tool authenticates safely and the platform's no-secret-in-logs promise holds for capabilities the same
way it holds for documents.

## Priority

P0 / Wave 1 — the security envelope for every external-touching family. 42-7/8/9 cannot ship their agent
path without it. **Carries the epic's biggest external dependency** (the reveal path — see below).

## The gap (READ FIRST)

No tool binds to a secret today; the six built-ins touch only the local repo/git/shell. Epic 29's
**`ISecretStore` now exists** (Story 29-1 — CLAUDE.md's "does not yet exist" note is **stale**), with
`SecretRef` (`scope`, `tenantId?`, `name`), `SecretScope` (`Platform`/`Tenant`), `SecretPurpose`
(`ApiKey`/`SigningKey`/`DbCredential`/HMAC/…), envelope-encrypted Postgres backend, and an
`ISecretAccessAuditor` on every access. **But by explicit design `ISecretStore` never returns plaintext
through a public signature** — plaintext reaches only a registered *rotation handler* via callback
(reveal to a human is the separate 29-3 reveal-once UX). A tool calling Hetzner/Slack needs the *live
plaintext* at execution time. **That path does not exist** — this is the reconciliation this story must
make.

## Scope

1. **Resolve a tool's `RequiredSecret` to a `SecretRef` per mode.** 42-1's descriptor declares
   `SecretRequirement(Purpose, Name, Required)`; 42-2's binding may override the logical `Name`
   (`secret_binding_name`). This story resolves that to a concrete `SecretRef`:
   - **single-user:** the user's secret — `SecretRef.ForTenant`/a user-owned scope per the single-user
     ownership model (the sole user owns their secrets).
   - **SaaS:** `SecretRef.ForTenant(tenantId, name)` — tenant-scoped, so tenant A's Hetzner key is never
     visible to tenant B (enforced by `ISecretStore`'s existing tenant-scoped authorization).
   A `Required` secret that does not resolve is a **loud, typed** "capability unconfigured" failure at
   resolve time (42-3 surfaces it) — never a tool that runs unauthenticated.

2. **The reveal-to-runtime-consumer path (the hard dependency).** Because `ISecretStore` won't return
   plaintext publicly, define an **`IToolSecretProvider`** seam that hands a tool a **short-lived,
   scoped credential** for one invocation without the tool ever seeing or storing the cabinet value.
   Two candidate backings (Open Question 1 in the epic README):
   - **(a)** an audited *reveal-to-consumer* extension of `ISecretStore` (building on 29-3 +
     `ISecretAccessAuditor`) that returns plaintext to a registered *tool* consumer under policy;
     **or (b)** an injection seam where the store performs the authenticated call *on behalf of* the
     tool (the tool never receives plaintext).
   **This story assumes (a) is filed as an Epic 29 story and hard-depends on it.** Until it lands, an
   external-touching tool resolves only in the human-assigned path (Epic 41 rule 4) — the agent path is
   dark but never silently unauthenticated.

3. **No secret in logs / events / output — enforced.** The credential is fetched immediately before the
   external call, held only for the call, and never placed in `ToolExecutionResult.Output`, in any DCB
   `TOOL.*` event args (42-5 redacts by the `RequiredSecret` field names + a value-match denylist),
   or in error messages (reuse `ErrorRedactor` / `SecurityHelpers`). A tool that would echo a bound
   secret field back is redacted before the result leaves `ExecuteAsync`.

4. **Access is audited through Epic 29's `ISecretAccessAuditor`.** Every tool secret fetch emits an
   Epic 29 access-audit row *and* a DCB `TOOL.SECRET_ACCESSED` event (ref storage key only, never the
   value) so the tool-use trail and the secret-access trail reconcile on `issueId`/`tenantId`.

## Acceptance Criteria

1. A tool's `RequiredSecret` resolves to the correct `SecretRef` per mode (test: SaaS → tenant-scoped
   ref for the run's tenant; single-user → the user's ref); a cross-tenant resolve is impossible (test
   asserts tenant A's run cannot resolve tenant B's secret).
2. `IToolSecretProvider` returns a short-lived credential for one invocation; a test asserts the
   credential is not retained after `ExecuteAsync` returns and never appears in the result.
3. A `Required` secret that is unconfigured fails loud/typed at resolve (test) — the tool is never
   invoked unauthenticated.
4. No bound secret value appears in `ToolExecutionResult.Output`, DCB event args, or error text across
   the family tools (test injects a known secret value and greps every emitted artifact for it).
5. Every tool secret fetch produces an `ISecretAccessAuditor` row + a `TOOL.SECRET_ACCESSED` DCB event
   carrying only the ref storage key.
6. With the reveal path **absent**, an external-touching tool's agent path is disabled and the step
   routes human-assigned (test with the reveal seam stubbed unavailable) — never a crash, never a silent
   unauthenticated call.

## Events

`TOOL.SECRET_ACCESSED` (ref storage key + purpose + tenant tag, **no value**). All other tool events are
42-5; this story defines only the secret-access one and the redaction rules the rest inherit.

## Single-user vs SaaS

- **single-user:** the sole user owns their secrets; the tool resolves the user-scoped ref.
- **SaaS:** secrets are tenant-scoped (`SecretRef.ForTenant`), owned by `tenant_admin`; a `member`-run
  agent uses the tenant's bound secret without seeing it. Tenant isolation is enforced by `ISecretStore`'s
  existing tenant-scoped authorization — this story adds no cross-tenant path.

## Dependencies

- **Epic 29 (hard):** `ISecretStore`/`SecretRef`/`SecretScope`/`SecretPurpose`/`ISecretAccessAuditor`
  **exist**; the **reveal-to-runtime-consumer capability does NOT** — this story **files that as a
  blocking Epic 29 story** (extension of 29-3). Recommend the user confirm direction (a) vs (b) before
  build.
- **42-1** (`SecretRequirement` on the descriptor), **42-2** (`secret_binding_name` override), **42-5**
  (redaction rules applied to the event args).
- **Unblocks:** 42-7/8/9 agent paths.

## Risks

- **The reveal path is the critical-path blocker.** If Epic 29 doesn't land it, the entire external-tool
  *agent* value is deferred (human-assigned still works). Mitigation: ship the resolution + redaction +
  human-assigned fallback in this story so that when the reveal seam lands, families flip on with no
  further security work.
- **Leak surface breadth.** Secrets can leak via output, events, errors, or a mis-typed `config` blob
  (42-2). Mitigation: a single choke-point redactor at the `ExecuteAsync`/event-emit boundary + the
  grep-for-value test (AC4) run against every family.

## Estimated Effort

Large (blocked-dependency + security-critical). ~4–5 days for the resolution/redaction/fallback in this
epic; the reveal path itself is Epic 29 effort, out of scope here.
</content>
