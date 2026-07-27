# Story 41-20: Scheduled Security Audit Workflow

Status: drafted

## User Story

As a **security** engineer (or eligible role-holder), I want a scheduled workflow that audits dependencies,
scans for exposed secrets, and checks compliance posture, producing a typed `Findings` report, so that
security drift is caught on a cadence and routed — not discovered during an incident.

## Priority

P1 / Wave 2 — recurring, high-consequence; complements the reactive 41-21.

## Scope

Scheduled sweep → thin binding over `document-lifecycle`. `consumes: [dependency manifest + advisories,
secret-scan surface, compliance checklist]` / `produces: Findings`. Produce cells
`(security, audit-dependencies)`, `(security, audit-secrets)`, `(security, review-compliance)` — run as
parallel lenses whose findings aggregate into one ranked `Findings` document.

## Produced document

`Findings`: each finding cites the vulnerable package/secret/control as evidence, with
severity + remediation; ranked; high-severity unmitigated ⇒ escalation.

## Events

`SECURITY_AUDIT.STARTED`/`.LENS`/`.REPORT` alongside `DOCUMENT.*`, tagged `repository`/`tenantId`.

## Orchestrator / user interaction

Accepted report routes per autonomy; each actionable finding is assigned to the owning role (dev for a
dep bump — can seed 41-12; security for a control gap); an exposed live secret is an always-escalate class
that also triggers `rotate-secret`.

## Autonomy behavior

- **70–84:** agent audits; security reviews before findings are actioned.
- **85–100:** agent audits and self-accepts; low-risk dep bumps auto-assigned; exposed-secret / high-CVSS
  always escalates regardless of dial.

> **Epic 42 caveat — the agent path works, but *ungoverned*.** Dependency/secret/compliance scanning
> is reachable only by shelling out through `ShellExecuteTool`; there is no audit-tool descriptor, no
> permission class, and no `TOOL.*` audit trail. That is the one path in this family that is possible
> today rather than impossible — but it is unclassified and unaudited until **Epic 42** lands.

## Acceptance Criteria

1. Scheduled, tenant-scoped, idempotent; each lens fail-closed (a lens failure is recorded, not dropped).
2. Findings cite concrete evidence with severity + remediation (context-gated `ValidateWithContext`
   rules — `Finding` gains optional `severity`/`remediation` members, required only for this story's
   producers). A clean lens emits **one all-clear finding** (`severity: "none"`, citing the scan
   output) — never an empty findings list, which `FindingsDocumentType` rejects with `EMPTY_FINDINGS`.
3. Exposed-secret path always escalates and integrates with `rotate-secret`.
4. `[ResumeBehavior(LatestStateReEntry)]`; 39-10 structural test green without allowlist.

## Dependencies

- **Blocking:** Epic 39 (`Findings`, lifecycle, store, task routing), `rotate-secret`, and **the tenant-aware scheduled-trigger seam — now owned by 41-30 (cadence AC only; the producing half is buildable before it)** (*corrected: "scheduler pattern" named no artifact;* `HourlyAnalyticsRollupScheduler` *is hardcoded to one workflow (`:198-199`), offers one `FireAtMinute` int rather than a window/cron shape (`:34`), threads no `tenantId` into the dispatch (`:202-203`), keeps its last-fired window in a per-process field (`:83`), and its advisory-lock key has no tenant component (`:241`) — one tenant's leader would suppress every other tenant's fire*).
- **Related:** feeds 41-12 dependency-upgrade planning.

## Estimated Effort

5–6 days
