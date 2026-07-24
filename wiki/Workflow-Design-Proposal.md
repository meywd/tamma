---
title: "Workflow: Design Proposal"
---

**Definition ID:** `design-proposal`
**Class:** `DesignProposalWorkflow`
**Source:** `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DesignProposalWorkflow.cs`

> **Epic 39 (Story 39-13) — now a `document-lifecycle` binding (produces `Design`).** This workflow is a thin binding over the generic [Document Lifecycle](Document-Lifecycle) (`produce → validate → review → revise → accept`). It dispatches `document-lifecycle` with `documentType = design` and the `(architect, propose-design)` producer cell, then exposes typed outcomes. The old bespoke pipeline — `llm-call` → hand parser (`DesignParsing`) → success-flag gate → error-`Finish` terminal — is **deleted**; the bespoke approve/reject bookmark gate is now the lifecycle's generic accept gate (the acceptor is an actor, routed by the orchestrator), and validation/review/revision/typed escalation are owned by the lifecycle. The `DESIGN.*` events still emit, now **alongside** the generic `DOCUMENT.*` events. The Flow Diagram, "Bookmark Points", and "Fail-Closed Parsing" details below describe the retired bespoke flow, kept for historical reference.

## Purpose

The Design Proposal workflow (Story 3.7) generates a technical design PROPOSAL for a complex requirement — a summary, multiple design alternatives with trade-off analysis, a recommendation, and a constraint evaluation — via the MEDIATED `llm-call` path (role=`architect`, action=`plan-system-design`; the engine holds no LLM credential). It DELIVERS the proposal to the issue via the mediated git seam, SUSPENDS on a bookmark awaiting a human approve/reject review decision (with a durable SLA timeout), then RESUMES via the secure resume endpoint and finalises: approved designs hand off to implementation, rejected designs capture the reviewer's feedback.

`DESIGN.*` DCB events are emitted at every transition so the design decision is fully auditable; proposals are versioned in workflow state + events (no dedicated table) and feed the Epic-32 learning loop. It reuses the [Clarifying Questions](/workflows/clarifying-questions) / [Assessment](/workflows/assessment) skeleton (llm-call → deliver → bookmark-wait → resume, fail-closed gates + error terminal).

## Flow Diagram

```
+------------------+
| Read Inputs      |
| (requirement,    |
|  constraints,    |
|  conventions)    |
+--------+---------+
         |
         v
+------------------+
| Generate Design  |
| Proposal         |
| (llm-call:       |
|  architect/plan- |
|  system-design)  |
+--------+---------+
         |
         v
+------------------+
| Parse Proposal   |
| (fail-closed)    |
+--------+---------+
         |
         v
+------------------+
| Proposal LLM OK? |
+--+------------+--+
  YES            NO
   |              |
   v              v
+----------+ +------------------+
| Emit     | | Emit DESIGN.     |
| DESIGN.  | | PROPOSAL.FAILED  |
| PROPOSAL.| | (LOUD)           |
| GENERATED| +--------+---------+
+----+-----+          |
     |                v
     v         +------------------+
+----------+   | LLM Call Error   |
| Deliver  |   | (Finish)         |
| Design   |   +------------------+
| Proposal |
| (git     |
|  seam)   |
+----+-----+
     |
     v
+------------------+
| Emit DESIGN.     |
| PROPOSAL.        |
| DELIVERED        |
+--------+---------+
         |
         v
+------------------+
| Wait For Design  |
| Approval         |
| (bookmark +      |
|  durable SLA)    |
+--+------+-----+--+
   |      |     |
Approved Rejected Timeout
   |      |     |
   v      v     v
+------+ +------+ +----------+
| Store| | Store| | Set      |
| Appr.| | Rej. | | Timeout  |
+--+---+ +--+---+ | Result   |
   |        |     +----+-----+
   v        v          |
+------+ +------+      v
| Emit | | Emit | +----------+
| APPR-| | REJ- | | Emit     |
| OVED | | ECTED| | REVIEW.  |
+--+---+ +--+---+ | TIMED_OUT|
   |        |     +----+-----+
   v        v          |
+------+ +------+      v
| Set  | | Set  | +----------+
| Appr.| | Rej. | | Expose   |
| Rslt.| | Rslt.| | Timeout  |
+--+---+ +--+---+ | Output   |
   |        |     +----------+
   v        v
+------+ +------+
|Expose| |Expose|
|Appr. | |Rej.  |
|Output| |Output|
+------+ +------+
```

## Bookmark Points

| Bookmark | Activity | Waits For | Outcomes |
|----------|----------|-----------|----------|
| `design-approval-{tenant}-{session}` | `WaitForDesignApprovalActivity` | Reviewer approve/reject decision via the secure resume endpoint, or the durable review SLA (`Design:ReviewTimeoutMinutes`, default 4320 = 3 days) | `Approved`, `Rejected`, `Timeout` |

The SLA is a durable `DelayFor` bookmark (EF-persisted, re-armed by `Elsa.Scheduling` after a host restart), so a never-reviewed proposal terminates as a real `Timeout` even across restarts. Whichever path resumes first completes the activity; Elsa burns the remaining bookmark.

## Secure Resume

- Public API: `POST /api/adl/design/resume` (`AdlEndpoints.ResumeDesign`, `WorkflowsManage` policy — SaaS member-role users get 403), which forwards to the engine's `DesignResumeEndpoint` (`/elsa/api/adl/design/resume`).
- One canonical bookmark-name builder is shared by the suspend and resume sides; the tenant id is server-derived and folded into the bookmark name, so a cross-tenant resume attempt simply 404s (no IDOR). 0 matching bookmarks → 404; more than 1 → 409.
- The reviewer identity is derived from the authenticated principal (non-forgeable), and the `Approved` flag is read via the tolerant `ResumeInput.AsBool` coercion so a serialized rejection is never mis-read as an approval.

## Sub-Workflows Dispatched

| Workflow | Wait? | Purpose |
|----------|-------|---------|
| `llm-call` | Yes | Proposal generation — role=`architect`, action=`plan-system-design`, tools disabled |

## Inputs

| Input | Type | Description |
|-------|------|-------------|
| `sessionId` | Guid | Session identifier (a new one is minted if empty) |
| `issueId` | string | Issue identifier |
| `requirement` | string | The complex requirement to design for |
| `repository` | string | Repository slug (owner/repo) for the issue-comment delivery |
| `issueNumber` | int | Issue number for the issue-comment delivery |
| `constraints` | string | Technical/business constraints to evaluate against |
| `conventions` | string | Repo conventions injected into the prompt |
| `tenantId` | string | Tenant id (GUID string, or empty in single-user mode) |

## Outputs

| Output | Type | Description |
|--------|------|-------------|
| `sessionId` | string | Session identifier |
| `status` | string | `approved`, `rejected`, or `timed_out` |
| `designProposal` | string | The serialized `DesignProposal` JSON (approved/rejected paths) |
| `approved` | bool | Whether the reviewer approved the design |

## Events Emitted

| Event | Status | When |
|-------|--------|------|
| `DESIGN.PROPOSAL.GENERATED` | success | A valid proposal was parsed (carries the alternative count) |
| `DESIGN.PROPOSAL.DELIVERED` | success | Proposal delivered to the issue (carries the channel) |
| `DESIGN.PROPOSAL.APPROVED` | success | Reviewer approved (carries feedback) |
| `DESIGN.PROPOSAL.REJECTED` | success | Reviewer rejected (feedback captured for revision) |
| `DESIGN.PROPOSAL.FAILED` | error (LOUD) | The generation `llm-call` failed or output was unparseable — never a fabricated design a reviewer would then approve |
| `DESIGN.REVIEW.TIMED_OUT` | error (LOUD) | Review SLA expired with no reviewer decision |

## Delivery Channels

`DeliverDesignProposalActivity` posts the proposal as a formatted review comment on the issue via the mediated git seam (`PATCH /api/v1/git/{repo}/issues/{n}`; the per-tenant git token is resolved API-side — the engine holds no git credential). Channel is `issue-comment` when repository + issue number are supplied; otherwise it falls back to `api` mode (the proposal is already durable in workflow state and the `DESIGN.PROPOSAL.GENERATED` event).

## Proposal Shape

```json
{
  "summary": "High-level summary of the recommended design (load-bearing)",
  "alternatives": [
    { "name": "Option A", "tradeoffs": "..." },
    { "name": "Option B", "tradeoffs": "..." }
  ],
  "recommendation": "Why the winning alternative wins",
  "constraintEvaluation": "How the proposal fares against the supplied constraints"
}
```

`DesignParsing.ParseProposal` fails closed (returns `null`) when the load-bearing `summary` cannot be recovered.

---

_See also: [LLM Call](/workflows/llm-call) | [Clarifying Questions](/workflows/clarifying-questions) | [Merge Approval](/workflows/merge-approval) | [Workflows Index](/workflows)_
