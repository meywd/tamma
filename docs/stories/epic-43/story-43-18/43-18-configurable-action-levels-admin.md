# Story 43-18: Configurable Action Levels — Platform-Scope Overrides of the Shipped Zone Table

Status: drafted

Implements: the product owner's 2026-08-03 direction that action levels — shipped constants under 43-11 — become configurable at the Tamma admin level, without weakening the zone model, the ceiling, or the toggle surface.

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **Tamma admin** — the sole user in single-user mode, the platform owner in SaaS,
I want to change the level at which any dial-governed action automates, with the shipped catalog value as the default, the override winning where one exists, and an audit event on every change,
So that re-leveling an action (the way `sprint-plan` moved 45 → 95 on 2026-08-03) is an administrative act with provenance, not a source edit and a release.

## Priority

P2 — the model must land first (43-11) and the surfaces that read levels must exist (43-15, 43-16). But the pressure is already real: the one re-level decided so far went through a story-file amendment and a code constant (43-11 changelog 1.7.0), and 42-10 already ships a *config-dependent* level (shell 80/40 by sandbox profile), so "the shipped table is not always the effective table" is true today with no single place that says so.

## Architectural Context (READ FIRST)

### 1. Where a level lives today, and who reads it

- **The shipped value**: `ActionDescriptor.DefaultMinAutonomy`, one constant per row in `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`, pinned verbatim by 43-11 AC4's level-table test. Static per process; 42-10 AC3 computes two rows (`tool:shell_execute`, `effect:process.spawn`) from the sandbox profile **at startup** — still "shipped", still static.
- **The readers**: the gate's principal-ladder fallback and its snapshot-degraded fallback (`apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:11-17` and the degraded branch reasoning at `:96-100`); 43-15's `levelOwned` predicate, detent set and `GET /api/actions/policy/diff` (43-15 AC3/AC5/AC6); 43-16's derived acceptor floor (`ShippedFloorFor` reading `ActionCatalog.Get(...).DefaultMinAutonomy` — 43-16 AC1/AC2); the policy and catalog views (`defaultMinAutonomy` at `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs:82`).

### 2. Storage: a new table, not another `action_assignments` row shape

43-5 built `action_assignments` (`apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs`) with three principal scopes; the platform scope (both principal keys null) is the **ceiling**, composed by `max()`. Reusing it for level overrides was considered and rejected — three reasons:

1. **Opposite semantics on the same column.** A platform assignment row's `MinAutonomy` can only tighten: "adding a row on either plane can only make the resolution more restrictive, never less" is the F10 invariant the evaluator documents and tests pin (`AutonomyGateEvaluator.cs:55-57`). A level override must be able to **lower** (that is the request). Putting replace-semantics into rows whose stated contract is tighten-only falsifies the invariant where it is written.
2. **The evaluator already names the one thing a plane may lower**: "The only value a plane may lower is the SHIPPED default when no other plane has an opinion" (`AutonomyGateEvaluator.cs:56-57`). An override *is* a replacement of the shipped default — so it belongs at exactly that rung: the last rung of the principal ladder and the snapshot-degraded fallback, where `ActionDescriptor.DefaultMinAutonomy` is read today. No composition operator changes; `max()` and `??` are untouched.
3. **The single-field-DTO discipline.** 43-6 built one-nullable-field writes precisely to keep one row from carrying two meanings (`ActionPolicyEndpoints.cs:15-18`, the 43-0 bug class). One row carrying both a ceiling and a shipped-default replacement is that bug class re-invited.

**So: a new control-plane table, `action_level_overrides`** — `(id, action_key UNIQUE, level int, note, version, created_by/updated_by, created_at/updated_at)`. It inherits `action_assignments`' residency posture wholesale and for the same reasons (`ActionAssignment.cs:3-46`): control-plane resident in both modes; **excluded from the destructive startup DROP list** (a safety table that silently reverts on restart is a governance surface that lies — extend `ActionGovernanceResidencyTests` to pin the exclusion); no FK to wiped tables; IF-NOT-EXISTS idempotent migration; **no numeric DB CHECK on `level`** (43-5 AC3/D5 — the single bound source is `AutonomyDial`, validated domain-side).

### 3. One resolver seam, so the surfaces cannot disagree

A new `IShippedLevelResolver` with one function: `EffectiveShippedLevel(ActionKey) = override ?? descriptor.DefaultMinAutonomy`, primed into `GovernancePolicySnapshotStore` alongside the assignment rows (same invalidate-on-write path every 43-6 write already uses — `ActionPolicyEndpoints.cs` doc comment), so the gate never does a per-decision read and a degraded read fails closed exactly as today. Every level reader goes through it:

- the gate evaluator's shipped-default rung and degraded fallback;
- **43-15**: `levelOwned`, the detent set (the distinct *effective* levels, or the detents go stale the first time an override lands between two shipped values), and the diff preview;
- **43-16**: the acceptance derivation — with the direct consequence, stated so nobody is surprised: an admin raising `document-type:design`'s level above the dial re-pins its acceptor to a person, and lowering it automates the acceptance, with no code change. That is the feature working, and 43-16 AC2's biconditional must hold over the *effective* level;
- the policy/catalog views, which grow `effectiveShippedLevel` and `levelSource: "shipped" | "override"` so the UI always shows provenance.

A guard keeps the seam single: an architecture test asserting no production code reads `DefaultMinAutonomy` except the resolver, catalog construction, and the pin tests.

### 4. Scoping — the CLAUDE.md two-mode rule, stated plainly

- **Single-user mode: the sole user IS the admin.** There is nobody else; the override surface is theirs (`SoleUserProvider` resolves the principal as everywhere else).
- **SaaS mode: the platform owner ONLY** — the routes take `PlatformOwnerAccess` (registered at `apps/tamma-elsa/src/Tamma.Api/Program.cs:1556`), the same policy the ceiling writes already use (`ActionPolicyEndpoints.cs:37-44,358-422`). **Tenant admins never see this surface.** Tenants get the dial and the per-action toggles (43-15); the level itself is platform vocabulary — one action, one meaning, for every tenant. A per-tenant level would make the same action automate at different levels for different tenants, splinter 43-15's detent math and 43-17's coverage map per tenant, and duplicate what tenants already have: tightening is a tenant assignment row, loosening is a toggle. Explicitly rejected, in scope of this story to document.

### 5. Audit — the family exists; use it

`ActionGateEventsService` already carries `ACTION.GATE.ASSIGNMENT_CHANGED` (`apps/tamma-elsa/src/Tamma.Api/Services/Actions/ActionGateEventsService.cs:42`) with `EmitAssignmentChangedAsync(scope, targetKind, targetKey, field, oldValue, newValue)` (`:215-238`), and the ceiling writes already call it with `scope: "platform-ceiling"` (`ActionPolicyEndpoints.cs:381,404,422`). Level writes emit the same family with `scope: "platform-level-override"`, `field: "level"`, both values, and the actor — best-effort like every assignment change (the row is the durable fact).

**Correction recorded while citing this**: 43-11 Out of Scope and 43-15 Out of Scope both still say "a platform-ceiling write path" does not exist ("still out per 43-6"). It does — `/api/admin/actions/ceiling` under `PlatformOwnerAccess`, `ActionPolicyEndpoints.cs:358-422`. This story amends those two lines with pointers (the 43-11 AC15 convention) rather than silently contradicting them.

### 6. Guard rails

1. **The 50-line floor.** An action whose shipped level is **≥ 50 may not be overridden below 50**. The rule is taken from the zone table itself, not invented: 50 is where the product owner's ladder stops being contained work and starts being consequences — the 50 zone is "Bypass PR checks", and everything at or above it is merges, deploys, external messages, unbounded execution, infrastructure, secrets, and deletes (43-11 Amendment 3 table). "A delete cannot be moved below 50" falls out: deletes ship at 95/100 ≥ 50. Actions shipped below 50 may move anywhere in the legal range — the contained half is exactly the half where the admin's judgement is cheap to reverse. The alternative — rejecting all lowering — was considered and rejected: the PO's request *is* re-leveling in both directions, and 42-10 already lowers shell 80 → 40 by profile, so lowering-by-configuration is established; the floor keeps the one direction that is never reversible cheaply (automating a consequence) inside the consequential band.
2. **Detent values only.** `level` must be a multiple of 5 in `[5, 100]`, or `AutonomyDial.AlwaysHuman` (101 — an admin may pin an action always-human; the stored-threshold meaning at `AutonomyDial.cs:38,48` already admits it). Arbitrary values would fuzz 43-15's detent set, whose premise is that the meaningful positions are finite.
3. **Machinery rows take no level.** An override on a machinery-inventory key is a 400 naming the classification — the same rule 43-13 AC5 applies to threshold writes, keyed on the same fixture. An uncatalogued key is a 404.
4. **The ceiling still wins.** Composition is unchanged, so an override below an existing platform ceiling row is legal but inert — the policy view's provenance must show both facts (`levelSource: "override"`, `source: "platform-ceiling"`), never a number that contradicts the gate.
5. **Drift-swept.** Every override row must reference a catalogued, non-machinery key **at startup and in the drift sweep** — an override stranded by a catalog change (e.g. a row for `effect:git.pull-request.merge` after 43-12 retires it) fails loudly with a named remediation (delete or re-key), instead of being silently ignored. The level shown anywhere in the UI is always resolvable to shipped-or-override with provenance; there is no third source.

### 7. Where it sits in the execution order

Per 43-11's execution order (changelog 1.6.0): **Wave D**, after Wave B (the levels must exist before they are overridable; the dial must validate 1–100 before a sub-70 override is even legal) and after 43-15 in Wave C (the `levelOwned` predicate and detent/diff surfaces must exist so this story changes *what they read*, not *what they are*). It can run alongside 43-14 and 42-10 in Wave D — with 42-10 there is one seam to coordinate: the profile-computed shipped value for shell/process is the *shipped* input to the resolver; an override wins over it like over any shipped value, and the floor rule keys on the effective shipped value in force (sandboxed 40 < 50 → free to move; unsandboxed 80 → floored at 50).

## Acceptance Criteria

1. **The table exists with the stated posture**: `action_level_overrides` per §2; the migration is IF-NOT-EXISTS idempotent; the table is on the DROP-list exclusion, pinned by an `ActionGovernanceResidencyTests` extension that reads the actual SQL literal (the 43-5 pattern). No numeric CHECK on `level` — asserted by the migration test.
2. **The admin surface exists and is scoped per mode.** `GET /api/admin/actions/levels`, `PUT` and `DELETE /api/admin/actions/levels/{ns}/{key}` — single-nullable-field DTO (`{"level": N}`), 43-6 style. In SaaS the routes take `PlatformOwnerAccess`; a tenant-admin caller (the `ActionsManage` holder who can write toggles) gets **403** — pinned in both directions in one test class. In single-user mode the sole user succeeds — pinned.
3. **Override wins, shipped is the default, through one seam.** `IShippedLevelResolver.EffectiveShippedLevel` returns `override ?? DefaultMinAutonomy`; the gate's shipped-default rung and its snapshot-degraded fallback both read it. Test: with dial 60 and an action shipped at 40, an override to 70 flips the gate to requires-human; delete the override and it flips back; symmetrically an action shipped at 70 overridden to 40 automates at dial 60. Both directions, through the real evaluator.
4. **Validation enforces the guard rails**: below-50 override on a shipped-≥50 row → 400 naming the floor rule and both numbers (the test case is a delete-family action at 95 → 45); non-detent value → 400; machinery key → 400 naming the classification; unknown key → 404; 101 accepted.
5. **Every change is audited**: `PUT` and `DELETE` emit `ACTION.GATE.ASSIGNMENT_CHANGED` with `scope: "platform-level-override"`, `field: "level"`, old and new values, and the actor id — asserted against the event repository, including the delete (new value null).
6. **43-15 and 43-16 read the effective level — pinned at their surfaces.** With an override in place: (a) the detent set and `GET /api/actions/policy/diff` reflect the effective level (an override moving an action across the `from`/`to` window changes the diff); (b) `levelOwned` and the 409 predicate key on the effective level; (c) 43-16's derived acceptor for a `document-type:*` row follows the override in both directions. Three tests, each through the public surface, all resolving via the one seam.
7. **Provenance is visible**: the policy and catalog views return `effectiveShippedLevel` and `levelSource` (`"shipped" | "override"`); 43-11's AC4 level-table pin keeps asserting the *shipped* constants unchanged (overrides live in the DB — the pin must stay green with overrides present, proving the two facts are separate).
8. **Stranded overrides fail loudly**: seeding an override for a non-existent key and booting fails startup with the remediation message; the drift sweep catches the same post-startup (catalog changed under a running row). Pinned.
9. **The single-source guard holds**: an architecture test proves no production call site reads `DefaultMinAutonomy` outside the resolver, catalog construction, and the pin tests.
10. **The two stale Out-of-Scope lines are amended** (43-11, 43-15 — the ceiling write path exists; see §5) with pointers to this story. `dotnet test` green; `dotnet ef migrations has-pending-model-changes` clean after the migration lands.

## Dependencies

- **Story 43-11** — the levels being overridden, the zone table the floor rule reads, and the 1–100 dial. Blocking.
- **Story 43-5** — the residency, snapshot and audit patterns this story copies (table **not** extended — see §2). Landed.
- **Story 43-6** — the endpoint file, DTO discipline, `PlatformOwnerAccess` precedent on the ceiling routes. Landed.
- **Story 43-13** — the machinery fixture AC4 rejects against. Blocking.
- **Story 43-15** — the `levelOwned` predicate, detents and diff this story re-points at the effective level. Blocking (Wave C before Wave D).
- **Story 43-16** — the acceptance derivation, third consumer of the seam. Blocking for AC6(c).
- **Story 42-10** — profile-computed shipped values; coordinate §7's shipped-vs-override layering (not blocking; the resolver treats the profile output as the shipped input).
- **Verified in tree**: `ActionCatalog.Descriptors.cs` (shipped constants); `AutonomyGateEvaluator.cs:11-17,55-57,96-100`; `ActionAssignment.cs:3-46`; `ActionPolicyEndpoints.cs:15-18,37-44,82,358-422`; `ActionGateEventsService.cs:42,215-238`; `Program.cs:1556` (`PlatformOwnerAccess`); `AutonomyDial.cs:38,48`.

## Out of Scope

- **Per-tenant levels** — rejected, with the reasoning recorded in §4. Tenants tune via the dial and toggles (43-15) and tighten via tenant assignment rows (43-5); the level's meaning stays platform-global.
- **Changing any shipped constant** — 43-11 owns the shipped table; this story adds the runtime layer over it.
- **A ceiling write path** — already exists (43-6/`ActionPolicyEndpoints.cs:358-422`); untouched here beyond the documentation correction.
- **Override UI beyond the existing admin page** — the 43-7/43-15 actions page gains an edit affordance on the level column for the admin principal only; a separate management screen is not built.
- **Group-level or mode-level overrides** — one override targets one action key; bulk re-leveling stays a reviewed set of single writes.
- **Two-person approval for an override** — no such mechanism exists anywhere in the repo (43-6 Out of Scope, still true).

## Estimated Effort

4 days — 1 for the table, migration, residency pin and endpoints with RBAC tests; 1 for the resolver seam, snapshot priming and evaluator rewire; 1 for the guard rails, audit and stranded-row sweep; 1 for the 43-15/43-16 consumer re-points and their three surface tests.

## Change Log

| Date       | Version | Changes                                                                                   | Author |
| ---------- | ------- | ------------------------------------------------------------------------------------------ | ------ |
| 2026-08-03 | 1.0.0   | Initial story — platform-scope level overrides: new `action_level_overrides` table (argued vs extending `action_assignments`), one `EffectiveShippedLevel` resolver seam consumed by gate/43-15/43-16, two-mode admin scoping (sole user / PlatformOwnerAccess, never tenant admins), 50-line floor + detent + machinery guard rails, ASSIGNMENT_CHANGED audit, ceiling-write-path doc correction | Claude |
