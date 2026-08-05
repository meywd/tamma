# Story 43-14: Approval Scopes and Grant Minting — One Approval System, Observed From Two Places

Status: done
Implements: Story 43-11 **Amendment 2, sections A (approval scopes), B (workflow approvals mint grants; correlation header threading) and C (chain rules, saga grants, monotonicity)**.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As a **person who just approved a merge or a deploy in the approval UI**,
I want that one "yes" to actually cover the run I approved — the API calls the workflow makes next, for the whole correlated chain, without a second ask per call,
So that approval is a decision, not a rubber-stamping treadmill, and a human "yes" is never followed by the system blocking its own approved action.

## Priority

P0 — Amendment 2-B documents the verified defect: workflow approvals (`MergeApprovalWorkflow`, `WaitForDeploymentApprovalActivity`) and the authorization ledger are **disjoint systems**. A person approves in the workflow, then Seam C 409s the actual API call, because no grant was minted and no shared correlation exists. Under the Amendment-3 zone numbers the merge example moves (merge.main = 65 automates at the default dial 70), but the defect shape is intact for everything above the dial — `deploy.prod` at 90 is the live case. Amendment 2-A adds the second axis: without correlation-standing grants, `tool:shell_execute` at 80 means one human ask **per tool call**, up to `MaxSteps = 20` turns per LLM sub-workflow (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:482`) — gating that interrupts tens of times per run stops being read, which degrades exactly the gates that matter.

## Architectural Context (READ FIRST)

- **The ledger today is single-use only.** `ActionAuthorization` (`apps/tamma-elsa/src/Tamma.Data/Entities/ActionAuthorization.cs`) is keyed by `(TenantId, UserId, CorrelationId, TargetKind, TargetKey)` (`:23`) with `CorrelationId` (`:39`) and `ConsumedAtUtc` (`:67`); `ActionAuthorizationLedger.TryConsumeAsync` (`apps/tamma-elsa/src/Tamma.Data/Repositories/ActionAuthorizationLedger.cs:132-190`) consumes by single-statement CAS — a second ask in the same correlation re-blocks. There is **no `Scope` column**.
- **Correlation never reaches the gate from workflows.** Seam C resolves correlation header → query → route-derived fallback and never the body (`apps/tamma-elsa/src/Tamma.Api/Infrastructure/GovernanceEnforcement.cs:209` `X-Tamma-Correlation-Id`, `:425-439` `ResolveCorrelationId`, `:444` `route:` prefix). Every engine mediation call carries its correlation in the **body** (`apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` — only a handful of GETs pass `?correlationId=`), and each sub-workflow sends its **own instance id, not the cycle id**. So no chain except deploy's Seam E has a ledger-visible shared correlation today.
- **The approval steps that must mint:**
  - `MergeApprovalWorkflow` (`apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/MergeApprovalWorkflow.cs:56`, definition id `merge-approval`, human bookmark `WaitForMergeApprovalActivity` at `:146-163`, decided via `POST /api/adl/{instanceId}/merge-approval` with RBAC).
  - `WaitForDeploymentApprovalActivity` (`apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForDeploymentApprovalActivity.cs:52`, bookmark at `:114-128`, `DEPLOY.PRODUCTION.APPROVAL_REQUESTED/APPROVED` events).
- **The changes:**
  1. **`Scope` on the grant**: `single-use` | `correlation-standing`. Single-use keeps today's CAS consume. Correlation-standing is satisfied for every ask matching `(principal, correlation, target)` without being consumed; it dies with the correlation (and carries the existing expiry).
  2. **Workflow approval steps MINT the corresponding grant.** The approve edge of `MergeApprovalWorkflow` mints correlation-standing grants for the merge composite — the per-target merge key (43-12), the issue-close/patch, and the in-composite branch delete (Amendment 2-C3: the 95 level belongs to the *standalone* delete route; the composite deletion rides the merge grant). The approve edge of `WaitForDeploymentApprovalActivity` mints for the deploy tail — `deploy.prod` and `git.release.create`. One approval system observed from two places, never two systems.
  3. **The cycle instance id is threaded as `X-Tamma-Correlation-Id`** on every `TammaApiClient` mediation call, sourced from the workflow correlation the cycle already carries — or correlation-standing grants have nothing to attach to. The `route:` derived fallback remains for calls with no run context.
  4. **Saga grants at entry** (Amendment 2-C2): deploy tail; secret rotation (already best-plumbed — `rot_{guid}` minted at entry, `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/Rotation/RotationTriggerService.cs:53`, threaded into the deferred retire); tenant move; tenant delete; the merge composite. Each saga's human entry approval mints correlation-standing grants for the chain's gated links.
  5. **Chain monotonicity as a test** (Amendment 2-C1): no gated link in a chain may carry a level above its chain's entry approval unless it has its own resumable human wait. Shipped as a fixture of chains (deploy tail, rotation, merge composite, tenant move/delete) × their links' levels, asserted against the catalog — a level edit that breaks a chain's monotonicity fails the build with the chain named.
- **Anti-rubber-stamp rule** (Amendment 2-A): per-call human approval is reserved for actions that fire once per run or less. Per-iteration actions (`shell_execute`, per the verified frequency classes) must be coverable by ONE correlation-standing ask at run entry — "this run may use the shell".

## Acceptance Criteria

1. **`Scope` lands on the ledger**: migration adds the column (default `single-use`, backfill-free — existing rows keep today's semantics); `TryConsumeAsync` consumes single-use rows exactly as today (existing tests unmodified) and satisfies correlation-standing rows repeatedly within their correlation without setting `ConsumedAtUtc`. Both paths pinned.
2. **The verified defect dies.** An end-to-end test: dial below the target action's level, human approves in `MergeApprovalWorkflow` (bookmark decided), the subsequent mediated call carrying the cycle correlation **passes** via `ReasonCoveredByAuthorization` — no 409. A control asserts the same call **without** the approval still 409s.
3. **Deploy tail is covered the same way**: `WaitForDeploymentApprovalActivity` approval → `deploy.prod` and `git.release.create` calls pass within the correlation; rejection mints nothing.
4. **The cycle correlation is on the wire.** `TammaApiClient` sets `X-Tamma-Correlation-Id` to the cycle instance id on every mediation POST/PUT; a test intercepts one call from a sub-workflow and asserts the header equals the **cycle** id, not the sub-workflow's own instance id.
5. **One shell ask per run, not per call.** With `tool:shell_execute` above the dial, the first tool-loop shell call raises one authorization ask; a granted correlation-standing scope lets subsequent shell calls in the same run pass without further asks. Pinned with a loop of ≥3 shell calls and an assert of exactly one pending row.
6. **Saga grants exist for all five chains** (merge composite, deploy tail, rotation, tenant move, tenant delete), each minted at the chain's entry approval with the chain's gated target keys; a fixture enumerates chain → minted targets and a test asserts the minting code matches the fixture.
7. **Chain monotonicity is a build-time test** over the fixture in the Architectural Context; deny-only tails (Seam D) above human-initiated heads are named violations unless covered by the head's grant.
8. **Grants are LLM-scoped.** Minted grants cover the gated LLM path only; a human caller never needs one (43-13). The mint records actor, workflow instance, and scope in the audit event.
9. **`dotnet test` green**; the only schema change is the `Scope` column.

## Dependencies

- **Story 43-9** — the ledger, Seam C, and the decide endpoint. Landed.
- **Story 43-11 / 43-12** — the levels and the per-target merge/deploy keys the grants name. 43-12 should land first or together, else the merge-composite grant names the coarse key and must be re-pointed.
- **Story 43-13** — the caller-kind predicate; grants attach to the LLM path.
- **Verified in tree**: `ActionAuthorization.cs:23,39,67`; `ActionAuthorizationLedger.cs:132-190`; `GovernanceEnforcement.cs:209,394-444`; `TammaApiClient.cs` (body-only correlation on mediation POSTs); `MergeApprovalWorkflow.cs:56,146-163`; `WaitForDeploymentApprovalActivity.cs:52,114-128`; `RotationTriggerService.cs:53`; `LlmCallModels.cs:482`.

## Out of Scope

- The dial UI's approve-rate telemetry (43-15 / Amendment 2-H).
- New approval UI surfaces — the existing bookmark/decide flows are reused, they just mint.
- Standing grants beyond one correlation ("always allow shell for this tenant") — that is the toggle layer (43-15), not a grant.

## Estimated Effort

4 days — 1 for the `Scope` column + ledger semantics, 1 for grant minting from the two approval steps + saga entries, 1 for the `TammaApiClient` header threading, 1 for the chain fixture, monotonicity test, and end-to-end pins.

## Change Log

| Date       | Version | Changes                                                                | Author |
| ---------- | ------- | ---------------------------------------------------------------------- | ------ |
| 2026-08-02 | 1.0.0   | Initial story — approval scopes, workflow-approval grant minting, correlation threading, saga grants, chain monotonicity (43-11 Amendment 2 A/B/C) | Claude |
