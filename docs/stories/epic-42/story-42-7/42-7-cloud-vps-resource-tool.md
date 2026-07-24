# Story 42-7: Cloud / VPS Resource Operations Tool (provider-abstracted)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As an **agent running an infra, incident, or capacity workflow**, I want a **provider-abstracted
cloud/VPS tool** to list, create, resize, and delete compute/resources — with delete/resize gated as
`Destructive` and bound to the provider credential — so that an `infra` task or a `41-22` rollback can
actually touch the VPS instead of shelling out unbound.

## Priority

P2 / Wave 3 — a tool family on the Wave-1 rails. Sequenced after 42-9/42-8 by Epic 41 demand (fewer 41
workflows need raw cloud ops than need HTTP/flags), but it is the family that finally replaces
`ShellExecute`-as-a-deploy-substitute for real infrastructure.

## Scope

1. **A provider abstraction, mirroring the Git/AI provider pattern.** Define `ICloudResourceProvider`
   (list / create / resize / delete / describe a resource) and one reference driver — **Hetzner**
   (the platform's own VPS host, per CLAUDE.md) — plus a **generic** driver seam so other providers grow
   the way the 7 Git platforms / 8 AI providers did. The tool (`CloudResourceTool : IToolExecutor`)
   dispatches to the configured provider; the LLM sees one tool with an `operation` + `resource` schema,
   not per-provider tools.

2. **Per-operation permission class** (the descriptor's class is the *ceiling*; the tool self-declares
   per-operation and 42-3 gates each call):
   | Operation | PermissionClass | Notes |
   |---|---|---|
   | `list` / `describe` | `ReadOnly` | safe at autonomy 70 |
   | `create` | `Mutating` | reversible (delete undoes it) |
   | `resize` | `Destructive` | risk of downtime / data movement |
   | `delete` | `Destructive` | irreversible → routed to orchestrator/human by 42-3 |
   Because a single `IToolExecutor` carries one `Descriptor`, model this as **operation-scoped
   descriptors** (the tool exposes the max class as its descriptor and reports the actual class per call
   to 42-3's gate) **or** split into `cloud_resource_read` (`ReadOnly`) and `cloud_resource_write`
   (max `Destructive`) tools — **recommend the split** so gating is per-tool-clean (see AC1).

3. **Secret binding.** Binds `SecretRequirement(SecretPurpose.ApiKey, "cloud/<provider>-token", Required)`
   via 42-4 — tenant-scoped in SaaS (tenant A's Hetzner project token ≠ tenant B's), user-scoped in
   single-user. The token never reaches logs/events/output (42-4/42-5).

4. **Long ops suspend.** A create/resize that the provider runs async sets the descriptor's
   `Suspends = true`: the tool starts the op, the workflow **suspends** (resumable-by-design), and
   resumes on the provider's completion (poll or callback) — reusing the platform's resumable pattern
   rather than blocking a worker thread. Failure is a loud `TOOL.FAILED` + the workflow's shared fail
   edge.

5. **Audit.** Every op emits 42-5 `TOOL.*` with `resourceId`/`operation`/`provider` tags (no token). A
   `delete` carries the authorizing actor (orchestrator/human) from 42-3 in its lineage.

## Acceptance Criteria

1. `list`/`describe` resolve as `ReadOnly` (callable at autonomy 70); `delete`/`resize` are
   `Destructive` and route through 42-3 authorization before execution (test per operation). If
   implemented as read/write split tools, the split is asserted; if operation-scoped, the per-call class
   reported to the gate is asserted.
2. The tool dispatches through `ICloudResourceProvider` to the Hetzner driver; a stub-provider test
   drives create → describe → delete without the tool knowing the concrete provider.
3. The provider token binds tenant-scoped in SaaS / user-scoped in single-user (42-4) and never appears
   in any emitted artifact (grep-for-value test).
4. A long create suspends the workflow and resumes on completion (integration test with a stubbed async
   provider) — no blocked worker thread, resumable across a crash.
5. A `delete` records the authorizing actor in its `TOOL.*` lineage (test).
6. Provider/transport failure yields `Success = false` + `TOOL.FAILED`, never a throw or silent success.

## Events

Reuses 42-5 `TOOL.INVOKED/SUCCEEDED/FAILED` with cloud tags. No new family.

## Single-user vs SaaS

- **single-user:** the user's cloud token; authorization of destructive ops routes to the single
  orchestrator/user.
- **SaaS:** tenant-scoped token; destructive ops route to the tenant orchestrator/role. A tenant's cloud
  operations and credentials never cross the tenant boundary.

## Epic 41 consumers

`deployment-pipeline` (infra tasks dispatched by 41-29), **41-22** (incident response / rollback —
recreate/replace a node), **41-23** (capacity & health review — list/describe to assess capacity).

## Dependencies

- **42-1** (descriptor, `Suspends`), **42-3** (per-op gating + destructive authorization), **42-4**
  (token binding — hard-blocked on the Epic 29 reveal path), **42-5** (audit).
- **Epic 41 / 41-29** (`infra` `TaskKind` dispatch → `deployment-pipeline`) as the consumer.

## Risks

- **Operation-scoped vs split-tool modeling** (AC1). A single descriptor can't express four different
  classes cleanly; the read/write split is the recommended resolution — flag for design sign-off.
- **Irreversible ops.** A wrongly-authorized `delete` is unrecoverable. Mitigation: `Destructive` +
  always-route-to-actor (42-3) + full lineage (42-5); consider an always-escalate acceptance-rules class
  for `delete` regardless of autonomy (Epic 39 policy, not tool code).

## Estimated Effort

Large. ~5–6 days (provider abstraction + Hetzner driver + suspend/resume + destructive gating wiring).
</content>
