# Story 39-20: Teams, Roles, Repo Access & Task Routing

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then, before writing any code, check the knowledge base:

- `.dev/spikes/` — existing research
- `.dev/bugs/` — known bugs
- `.dev/findings/` — pitfalls and best practices
- `.dev/decisions/` — architecture decisions

## User Story

As a **tenant with multiple repos, multiple users, multiple teams, and multiple user roles**,
I want **a first-class access model — users belong to teams, teams and users have roles and permissions, repos have access grants — and a task-routing rule built on it: a user receives tasks only for workflows they initiated or repos they have access to**,
So that in a real organization the orchestrator's task assignments, the Task View, chat answers, and workflow initiation all respect who may see and act on what — and the single-user deployment remains the trivial case of the same model, not a separate code path.

## Priority

P0 — Every user-facing piece of the epic routes through this: the orchestrator's autonomy routing (39-5/39-17) must pick assignees from an eligibility set this story defines; 39-19's two surfaces filter by it; 39-18's channel groups must not deliver a task to an ineligible user.

## Architectural Context (READ FIRST)

**What exists today (extend, don't replace):**

- Tenant-level role hierarchy `member < admin < owner` with the permission matrix in `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs` (`Permissions.Matrix`, `"workflows:manage"`-style keys) — the enforcement seam every endpoint already uses.
- `users` / `user_invites` tables (`apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs`); org membership endpoints (`OrgEndpoints.cs`).
- **No teams, no repo-scoped access grants** — tenant membership is currently flat, and repo visibility is effectively "everything in the tenant." That flatness is what this story removes.
- The two-scoping-models rule (CLAUDE.md): in single-user mode the sole user owns everything — the model must collapse to "auto-owner of a personal tenant, member of no teams, access to all their repos" with zero special-casing in consumers.

**The visibility rule (the story's contract, verbatim from design review):** a user receives tasks for (a) **workflows they initiated** (the `initiatedBy` tag stamped at dispatch — 39-19 AC2), OR (b) **repos they have access to** (via direct grant or team membership). Everything downstream — Task View listing, channel delivery, assignment eligibility — derives from this one predicate, implemented once.

## Acceptance Criteria

1. **Teams model.** `teams` + `team_members` (tenant-scoped): a team has a name and role defaults; users join with a per-membership role. CRUD via tenant endpoints gated `users:manage`-style (owner/admin); events on membership changes. Single-user mode: no teams required for full function.

2. **Repo access grants.** A repo registered to a tenant carries access grants: to teams and/or individual users, with an access level (at minimum `read | contribute | admin` — closed enum). Default posture decided and documented (existing tenants' repos grandfathered as tenant-visible to avoid a breaking lockout, with a migration note). Admin UI + endpoints to manage grants (owner/admin).

3. **One eligibility resolver.** An `ITaskAudienceResolver` (name illustrative) implementing the visibility predicate — `CanSee(user, task)` ⇔ user initiated the task's workflow ∨ user has access to the task's repo — plus `EligibleAssignees(task)` (the ordered candidate set the orchestrator picks from, filterable by role/permission requirements a task type declares, e.g. "acceptance of a Plan requires `workflows:manage`"). Both directions covered by tests including the team-transitive case (user in team, team granted on repo). All consumers (39-18 channel groups, 39-19 Task View, orchestrator assignment) call this resolver — no consumer re-implements the predicate, asserted by an architecture test where feasible.

4. **Enforcement at every surface.** Channel delivery (39-18): hub group membership per task computed from the resolver, server-side. Task View (39-19): listing filtered by it. Chat: answers/actions about a repo the user lacks access to are refused (39-19 AC1/AC2). Workflow initiation: requires the initiating user's permission on the target repo. Orchestrator assignment (39-17): only from `EligibleAssignees` — the agent literally cannot address a task to an out-of-set user (server-validated, not agent-honor-system).

5. **Task events.** `TASK.ASSIGNED`, `TASK.REASSIGNED`, `TASK.COMPLETED` DCB events (AGGREGATE.ACTION.STATUS convention) carrying assignee, the eligibility basis (`initiator | repo-access`), the autonomy context reference, and `issueId`/`documentId` tags — so "who was asked, why them, who answered" is auditable from the stream.

6. **Permission matrix extension, not fork.** New permission keys land in `Permissions.Matrix` following the established pattern (`repos:manage`, `tasks:assign`, …, each with a comment naming this story); the role hierarchy stays `member < admin < owner`; team-level roles compose with (never bypass) tenant roles — the effective permission is the union of tenant-role grants and team grants scoped to that team's repos, and the composition rule is documented + tested.

7. **Two scoping models proven.** Integration tests run the same scenarios in single-user mode (sole user sees/does everything, zero teams) and SaaS mode (two teams, disjoint repos, one shared workflow initiator) and assert the visibility sets — the CLAUDE.md universal rule applied to access itself.

## Technical Notes

- Repo identity: reuse however repos are registered today (platform installations / `.tamma` config) — this story adds the *grants*, it does not re-model repo registration. If repo registration is currently implicit, the minimal explicit `repositories` row this story needs is in scope; note it in the schema doc.
- The resolver must be cheap (called on every channel delivery and task listing): design for a per-user precomputed scope (user → repo-id set, invalidated on grant/team changes), not a per-check join cascade.
- Assignment strategy (round-robin, load, role preference) is orchestrator policy (39-5 rules / 39-17 reasoning) — this story supplies the SET, not the CHOICE.
- Migrations: additive; `dotnet ef migrations has-pending-model-changes` clean; config in `TammaModelConfiguration.cs`.

## Dependencies

- **Prerequisite (in place):** `Permissions.Matrix` + auth middleware, `users`/org membership, `ITammaModeProvider`.
- **Lockstep:** 39-19 (surfaces that consume the resolver), 39-18 (channel groups), 39-17 (assignment validation).
- **Feeds:** 39-5 autonomy routing (eligible-assignee input), Task View, chat authorization.

## Estimated Effort

6–8 days

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-07-20 | 1.0.0   | Initial story creation | Claude |
