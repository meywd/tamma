# Story 42-3: Per-Tool Permission & Autonomy Gating

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **orchestrator dispatching a workflow step**, I want `ResolveToolsActivity` to hand the agent
**only the tools it is permitted to use, for its role, at the current autonomy level** — and to route
any destructive or above-floor tool to me (or a human) via the acceptance channel instead of letting
the agent fire it unattended — so that a tool call carries the same governance as accepting a document:
low-risk reads run free at high autonomy, and flipping a prod flag or deleting a VPS is a decision taken
by an actor, never an unattended side effect.

## Priority

P0 / Wave 1 — the **enforcement** story. 42-1 lets a tool declare its class/floor and 42-2 stores
per-principal overrides; this story is where those become behavior. Every family (42-7/8/9) and MCP tool
(42-6) is inert-but-safe until this gates them.

## The gap (READ FIRST)

`ResolveToolsActivity` (`Tamma.Activities/LlmCall/ResolveToolsActivity.cs`) resolves tool **definitions**
from config + a built-in switch and returns `List<ResolvedTool>`. It has a `ToolNamesInput` (the tools a
step wants) and a `ProviderName` — but it applies **no permission, role, or autonomy filter**. Whatever
names a step requests are resolved and handed to the LLM. `ToolExecutorRegistry.GetAllowed(allowlist)`
filters by *name only*. `ToolCallValidator` checks name/format/args/`ActionGate` at call time but knows
nothing about who the agent is or the autonomy dial. So **there is no per-tool permission or autonomy
gate anywhere** — the accept gate governs *documents*, not *tool calls*.

## Scope

1. **Gated resolution in `ResolveToolsActivity`.** Add inputs for the resolution context — the agent's
   `AgentRole`, the principal (userId/tenantId + mode), and the current autonomy level (read live from
   the acceptance-rules/autonomy config, never cached — Epic 39 rule). For each requested tool name,
   resolve via 42-2's `IToolBindingResolver` to `{ enabled, autonomyFloor, allowedRoles }` (falling
   through to the 42-1 descriptor), then keep the tool **only if**: `enabled` **and** the agent's role
   ∈ `allowedRoles` **and** `autonomyFloor ≤ currentAutonomy` **and** `PermissionClass != Destructive`.
   The returned `List<ResolvedTool>` is the **eligible callable set** the agent sees.

2. **Route the ineligible-but-needed tools, don't silently drop them.** A requested tool that is
   `Destructive`, or `autonomyFloor > currentAutonomy`, is **not** dropped from the plan — it is
   surfaced as a **required-but-gated capability**. The step publishes an authorization request on the
   **existing** workflow↔orchestrator channel (Epic 39's `AcceptanceRequest` machinery — see Open
   Question 3 on whether it is the accept family verbatim or a sibling `ToolAuthorizationRequest`) and
   **suspends** (resumable-by-design). The orchestrator, reading the autonomy dial + acceptance rules,
   either authorizes (itself, at high autonomy for the allowed classes) or assigns the decision to a
   holder of the appropriate tenant role — landing in their Task View (Epic 39 39-20 scoping). This is
   the **"acceptor is an actor, not a branch"** model applied to capabilities: a destructive tool call
   is authorized by someone, exactly like accepting a document. On authorization, the tool becomes
   callable for that bounded invocation; on denial the step takes the loud handoff edge.

3. **Workflow/cell declares the tools its step needs.** Keep `ToolNamesInput` as the declaration point
   (a step/producer says "I need `deploy_control`, `feature_flag`"). This story makes that declaration
   *resolve to the permitted+eligible subset* per agent+principal rather than a blind pass-through. A
   step that needs a tool it may never get (role not permitted at all) fails at resolve with a loud,
   typed "capability not granted" — never a silent empty tool list that makes the agent hallucinate the
   action succeeded.

4. **Call-time defense-in-depth.** `ToolCallValidator` already runs at invocation. Extend its allowlist
   check to also assert the tool is in the *eligible* set for the run (not just name-format-valid) — so a
   model that fabricates a tool-call for a gated tool is rejected with the same posture as an
   off-allowlist call. Belt-and-suspenders with the resolve-time filter.

## Acceptance Criteria

1. `ResolveToolsActivity` returns only tools that are enabled, role-permitted, autonomy-eligible, and
   non-`Destructive` for the run context (table-driven test across roles × autonomy levels × classes).
2. Autonomy is read live: two runs of the same step at autonomy 72 vs 95 resolve different eligible sets
   for a tool with floor 85 (test) — the value is not captured into the workflow state.
3. A requested `Destructive` (or above-floor) tool triggers an authorization request on the acceptance
   channel and the step suspends; a test drives the authorize path (tool becomes callable) and the deny
   path (loud handoff), asserting no unattended execution occurs before authorization.
4. A step requesting a tool the agent's role is **never** granted fails loud/typed at resolve — asserted
   not to produce an empty-but-successful tool list.
5. `ToolCallValidator` rejects a fabricated call to a gated/ineligible tool at invocation time (test),
   independent of the resolve-time filter.
6. single-user and SaaS both enforce correctly: single-user reads the user's bindings, SaaS the
   tenant's; a SaaS `member`-run agent gets the tenant_admin's resolved grants (no per-user layer).

## Events

`TOOL.RESOLVED` (eligible set size + gated count, per run), `TOOL.DENIED` (role/autonomy denial),
`TOOL.ESCALATED` (routed to orchestrator/human for authorization), `TOOL.AUTHORIZED` /
`TOOL.AUTHORIZATION_DENIED` (the decision outcome). All tagged `issueId`/`tenantId`/`toolName`/
`permissionClass`; emitted via the standard `TammaEventEmitter` drain. (The invocation-level
`TOOL.INVOKED/SUCCEEDED/FAILED` are 42-5.)

## Single-user vs SaaS

- **single-user:** the sole user's `tool_bindings` (42-2) drive role/floor/enablement; authorization of
  a gated tool routes to the single orchestrator/user.
- **SaaS:** the tenant's bindings drive it; authorization routes to the tenant orchestrator or a holder
  of the appropriate tenant role — hard-scoped to the tenant (tools, context, and the acceptance channel
  never cross the tenant boundary, per Epic 39's orchestrator-per-tenant model).

## Dependencies

- **42-1** (`PermissionClass`/`AutonomyFloor` on the descriptor), **42-2** (`IToolBindingResolver`).
- **Epic 39:** the `AcceptanceRequest` workflow↔orchestrator channel + autonomy dial + Task View (the
  authorization routing rides these unchanged).
- **`Tamma.Core/Agents` `AgentRole`** for the role check (and Epic 41-1's added roles once landed —
  `ux_designer`/`scrum_master`/`project_manager` — extend the grantable set with no change here).
- **Unblocks:** every tool family (42-7/8/9) and MCP (42-6) — they are governed the moment they declare
  a descriptor.

## Risks

- **Reusing vs. forking the accept gate.** Routing a tool authorization through `AcceptanceRequest`
  must not duplicate acceptance semantics. Mitigation: resolve Open Question 3 up front — prefer reusing
  the accept-gate actor/suspend/Task-View machinery with a `ToolAuthorizationRequest` payload so tool
  authorizations appear in the existing inbox with no new surface.
- **Silent empty tool list.** The classic failure is handing the agent zero tools and letting it claim
  success. AC4 pins this as a loud, typed failure.

## Estimated Effort

Large. ~5–6 days (touches the hot resolve path + a new suspend/authorize edge; heavy cross-mode +
cross-autonomy test matrix).
</content>
