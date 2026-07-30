# Epic 43: The Action Catalog — one governed list of everything Tamma can do

## Overview

Tamma has an autonomy dial (`AcceptanceRules.AutonomyLevel`, 70–100). It is stored, validated,
defaulted, serialized and displayed — and **nothing reads it to decide anything**. There are 11
production references and every one is a declaration, a default, a DTO field or an audit tag. The
one component that would consume it, `AcceptanceGuardrails.TryPreGate`, has zero production call
sites.

That is not a defect. The consuming layer was never built — it is this epic.

**What this epic builds:** one catalog of every consequential action Tamma can take, partitioned
into assignable groups, where an admin decides per group or per action **at what autonomy level the
system may do it by itself, and below which a person decides**. Plus the gate that enforces it, the
admin surface that edits it, and the build-time guard that keeps a new capability from shipping
unclassified.

**The product requirement, verbatim:**

> "a list of actions done by the system should be created, like a permissions list, the list should
> be under action categories or groups where it can be assigned as a whole, so the admin can set at
> each level what groups or individual actions are set to automate and what for users to do, even
> those automated at 70 should be listed and greyed out so a future lower automation is doable"

## Why there is nothing to extend

Consequential capability is spread across **sixteen unrelated vocabularies**, none of which is a
list of "actions" and only two of which have drift protection:

| Vocabulary | Count | Canonical list | Drift-guarded |
|---|---|---|---|
| `AgentAction` (work phases) | 80 | `Tamma.Core/Agents/AgentAction.cs` | Yes — strongest in repo |
| `AgentRole` | 8 | `Tamma.Core/Agents/AgentRole.cs` | Count-pinned |
| `(role, action)` eligibility cells | 93 | `RolePhaseMap.cs:43-163` | Yes, 3 layers |
| `DocumentTypeKey` | 10 | `DocumentTypeKey.cs` + `DocumentTypeRegistry.cs` | Yes — fail-loud index |
| `IToolExecutor` implementations | 7 (6 DI-registered) | class declarations only | **None** |
| Elsa `[Activity]` types | 218 across 23 categories | attribute decoration | None; 13 SecretsRotation activities carry no `[Activity]` at all |
| HTTP routes | ~388 total, ~205 mutating | `Program.cs` route table | None |
| API permission strings | 18 | `Auth/Permissions.cs` | Pinned by test |
| Authorization policies | 22 | `Program.cs:1518-1696` | 3-place lockstep |
| `SensitiveActionCatalog` | 53 codes × 11 categories | `Tamma.Core/Audit/` | None — and covers zero code-mutating actions |
| Elsa workflow definitions | 45 | hardcoded `DefinitionId` strings | 16 edges pinned |
| Platform task types | 8 | `IPlatformTaskHandlerRegistry.cs` | Yes — startup throws on duplicate |
| `PlatformCapability` | 11 | `PlatformCapability.cs` | — |
| Permitted git subcommands | 14 | a private `HashSet` in `GitOperationsTool.cs:21-25` | None |
| Provider allowlist | 15 | `ProviderAllowlist.cs` | — |
| Shell denylists | two, non-overlapping, never merged | `CommandValidator.cs` (16 regexes), `ActionGate.cs` (20) | None |

There is also `packages/gates/src/permissions/types.ts:10-17`, which defines exactly the shape this
epic needs — `PermissionCategory = tool|file|command|api|git|resource`,
`PermissionAction = allow|deny|require_approval`. It is dead legacy TypeScript with no C# consumer.
A design reference, not reusable code.

**So the catalog is a new union vocabulary layered over the existing namespaces, not a
re-presentation of an existing list.**

---

## The model

### 1. The catalog: a composite key, not a flat enum

Home: `apps/tamma-elsa/src/Tamma.Core/Actions/`. Core is the only assembly reachable from
`Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` alike — it has **zero**
`ProjectReference`s. `AgentAction` already lives there for the same reason.

```csharp
[JsonConverter(typeof(WireEnumJsonConverter<ActionNamespace>))]
public enum ActionNamespace {
    [Wire("agent-action")]  AgentAction,   // key = AgentAction wire       — 80
    [Wire("document-type")] DocumentType,  // key = DocumentTypeKey wire   — 10
    [Wire("tool")]          Tool,          // key = ToolAction wire        —  8  (new)
    [Wire("effect")]        Effect,        // key = ExternalEffect wire    — 22  (new)
    [Wire("automation")]    Automation,    // key = BackgroundActor wire   — 25  (new)
    [Wire("platform-task")] PlatformTask,  // key = PlatformTaskKind wire  —  8  (new)
}

public readonly record struct ActionKey(ActionNamespace Ns, string Key) {
    public string ToWire() => $"{Ns.ToWire()}:{Key}";   // "agent-action:deploy", "tool:file_write"
}
```

A flat ~153-member enum would copy all 80 `AgentAction` wire strings into a second vocabulary — the
exact drift this epic exists to prevent. The composite shape is already shipped and validated:
`AcceptanceRules.ValidateEscalationClass` (`AcceptanceRules.cs:120-147`) switches on a kind and
delegates key parsing to the owning registry. This is that switch with four more arms.

`ActionNamespace` deliberately preserves the two wire strings `EscalationClassKind` already uses, so
`agent-action:` and `document-type:` keys are a strict superset of a vocabulary already persisted in
`acceptance_rules_overrides`.

**`EscalationClass` / `AlwaysEscalate` are not deleted.** They ship, they are in the admin DTO, in
the admin UI, and there is a live production default (`TriageBindingHelper.cs:157` ships
`EscalationClass(AgentAction, TriageIntake)`). They are absorbed as a floor (§4), not removed.

### 2. Groups

`ActionGroup` — a `[Wire]` enum, **16 members**, a strict partition: every action in exactly one
group. Membership is a field on the descriptor; the by-group index is *built*, never hand-maintained
(the `RolePhaseMap.cs:170-171` idiom). No `[Category]` attribute is introduced — none exists in the
repo and inventing one creates a second grouping idiom beside the shipped one.

`planning-and-analysis` · `authoring` · `review-and-acceptance` · `docs` · `code-read` ·
`code-write` · `command-execution` · `ci-and-test` · `source-control-read` · `source-control-write` ·
`issue-tracking` · `deploy-control` · `external-comms` · `model-invocation` · `secrets` ·
`platform-automation`

The index build throws if any group has zero members, so a group cannot rot into a dead label.

**Assigning all 80 agent-actions to groups is the single largest judgment call in this epic.** A bad
partition is a bad safety policy, and no test can catch a wrong-but-consistent one. It gets
disproportionate review relative to its estimate.

> **Corrected 2026-07-25.** This section said "15 members" while listing sixteen names, and did not
> say which to drop. It is **16** — merging two groups to hit a round number is precisely the
> wrong-but-consistent partition this story exists to avoid. Settle any change before Story 5, since
> group wire strings become persisted vocabulary the moment assignments are stored.

> **Corrected 2026-07-25 — the shipped-default set was materially wrong, and enforcing-v1 made it
> dangerous.** An earlier draft (inherited from the design) said the `AlwaysHuman` defaults include
> "the 10 `document-type:*` where `AcceptanceDefaults.For` ships `AcceptorRequirement.Human`."
> **It ships exactly one.** `AcceptanceDefaults.cs:128-133` maps `Plan`/`Review` to panel *selection*
> — which is not a human acceptor — and only `Design` to `s_humanAcceptorRules`;
> `AcceptorRequirement.Human` occurs once in production, at `:115`. Because v1 enforces (D1), taking
> the design at its word would have **gated nine document types the day the catalog shipped**, under
> a decision whose entire point is to change no runtime behaviour. The derived set is one member.
> That will look wrong to a reviewer, so the evidence is restated in Story 3, its plan, and the test
> comment.

### 3. The policy value: one number per action

**One integer per (target, principal): `MinAutonomy`. Automated iff `currentDial >= MinAutonomy`.**

Not a level×action matrix. A matrix is 31 levels × ~153 actions = **4,743 cells per principal** —
unusable in a UI, and it admits incoherent non-monotone policy ("human at 80, automated at 70"),
which is not a dial. Decisively, for the future-lower-floor requirement: widening the floor to 0
costs **zero** backfill under a threshold (`MinAutonomy = 85` means the same thing under either
range) and **70 new cells per action per principal** under a matrix.

**It still gives the per-level editing the requirement asks for.** The admin views the catalog at
`?level=L`; flipping a row automated→human writes `MinAutonomy = L + 1`, human→automated writes
`MinAutonomy = L`. The admin never types a number. **Per-level editing, level-independent storage** —
which is exactly what "even those automated at 70 should be listed and greyed out so a future lower
automation is doable" requires. Every action carries an assignment regardless of the current floor;
nothing is absent from the model because of where the floor happens to sit.

`AlwaysHuman = AutonomyDial.Max + 1` is a legal value meaning "a person decides at every level in
the range" — not a nullable, not a magic number.

**"No opinion" is the absence of a row**, not a nullable column. DELETE removes the row and the next
tier takes over, mirroring `AcceptanceRulesService.DeleteAsync`.

### 4. Resolution

```
effectiveMinAutonomy(action, principal) =
    max(
        platformCeiling(action),           // platform-scope rows: action → group → no ceiling
        legacyAlwaysEscalateFloor(action), // §4 below
        principalLadder(action)            // first present of: action row → group row → shipped default
    )
```

`max()` composes because the encoding is monotone — a higher number means more human involvement — so
**the platform can only tighten, never loosen. A tenant admin cannot lower a platform gate.**

Inside the principal ladder, **an action override beats its group override outright** (`??`, not
`max()`) — that is what "individual actions overriding their group" means, and it is what
`AcceptanceRulesService.cs:52-64` already does. The consequence is recorded as a risk: an admin can
lower one action below its group. Mitigated by provenance badges, an audit event, and a confirm
dialog when lowering a `Destructive` action.

`Enforce`, `Enabled` and `AllowedRoles` resolve **per field, independently**, on the same ladder.
All three columns are **nullable** so "unset" is representable — a non-nullable `Enabled DEFAULT
TRUE` would make a threshold-only write silently re-enable a group-disabled action.

Every resolved row carries its provenance: `platform-ceiling` | `always-escalate-legacy` |
`action-override` | `group-override` | `system-default`.

### 5. Absorbing the existing always-escalate list

`AcceptanceRules.AlwaysEscalate` is shipped, DTO-exposed, UI-editable and has a live production
producer — and is currently inert. Two admin surfaces recording "this always goes to a person", one
of which does nothing, is a trap.

Fix, with no deletion and no migration: the gate evaluator calls `AcceptanceGuardrails.TryPreGate` —
**giving it its first production call site** — and if it escalates for a class mapping to this
`ActionKey`, contributes `AlwaysHuman` as a floor. Because it composes with `max()`, a legacy entry
is a floor the new surface **cannot lower**; only deleting it in the acceptance-rules UI can. The
catalog UI renders such rows with `source: always-escalate-legacy`, a deep link, and a "migrate to a
catalog row" affordance that writes the equivalent row and does *not* auto-delete the legacy entry.

`TryPreGate` also implements an unrelated rounds-exhausted short-circuit; the gate takes only the
always-escalate contribution and ignores the rounds outcome. The document lifecycle keeps owning
rounds.

---

## Drift prevention

**A new capability that is not in the catalog must not be mergeable.** That is the whole guarantee.

The checks are **bidirectional**: code that performs an action must have a catalog entry, and a
catalog entry must have a performing site. The catalog is derived from the code, so both hold when
it is written; if code is later deleted, the check tells you to delete the entry. The one judgment
call at authoring time is **not to catalogue a placeholder workflow as a real capability** — a
workflow that does not yet do the thing gets no entry until it does.

Mechanisms, all existing house patterns:

- **Fail-loud index build.** Adding an `AgentAction` member without a descriptor is a boot failure —
  the `PromptFileLoader` posture, already proven at 101 files. Intentional, and it will bite the
  first developer who adds an enum member and runs the app before the tests.
- **Fail-loud tool-vocabulary validator at startup** — the check that has never existed. Today three
  tool vocabularies disagree: the registry (`file_read`/`file_write`/`shell_execute`), the per-role
  agent config advertised to the model (`Read`/`Write`/`Bash`), and a dead built-in map. Reconciling
  them is a **privilege expansion, not a cleanup** — those tools currently cannot execute for roles
  advertising Claude-Code names.
- **Count pins and wire pins** on the closed vocabularies this epic authors.

> **Shipped by 43-4, and what it deliberately does NOT close.** The validator above is live
> (`ActionCatalogStartupValidator`, Tamma.Api host only — the engine registers no tools), and
> `ToolNameAliases` now resolves both vocabularies onto `tool:*` keys **for policy only**. The
> divergence itself remains open by design: the advertised Claude-Code names
> (`Read`/`Write`/`Edit`/`Bash`/`Grep`/`Glob`) **still do not execute** — `ToolExecutorRegistry` is
> keyed on the registry names and `ManagedAgent.ToResolvedTools` still advertises byte-identical
> names (pinned by `AdvertisedToolNamesUnchangedTests`, including a source scan that fails if the
> alias map is ever wired into the advertisement path). Making those names execute is a **privilege
> expansion** — five roles' tool surfaces would go from inert to live — and is filed as a separate
> story outside Epic 43 with its own review; it must not ride a validator or an alias map.
- **Reflection over real call sites** — the only mechanism that checks the catalog against reality
  rather than against another declaration. Covers mutating routes (including attribute-routed
  controllers and hub endpoints, which no syntax analysis can see), the mediation client's mutating
  methods, background actors (including the one registered by factory overload, where
  `ImplementationType` is null), and activities missing `[Activity]`.
- **Shrink-only ratchets with staleness detection** on the ungoverned-endpoint backlog.

**No Roslyn analyzer.** It was designed and rejected on evidence: **79 of the 200 mutating `Map*`
calls in `Program.cs` terminate on the same line with no fluent chain to inspect** — a ~40%
structural miss rate — and `MapControllers`, `MapHub` and Elsa's `UseWorkflowsApi()` are invisible to
syntax analysis entirely. An analyzer with a 40% blind spot is worse than none, because a
completeness guarantee that is not complete is exactly the failure mode this epic exists to prevent.
CI blocks the merge either way; what is lost is local-build feedback.

**Holes the mechanisms honestly cannot close** — recorded, not hidden:

- Bindings attach to a **site**, not an effect. A new capability grown inside an
  already-governed method passes every check.
- **The gate matches on identity, not on argument values.** An `ActionKey` either is or is not
  gated; there is no way to express "gate this action *when* the payload looks like X". This is the
  same limitation `EscalationClassKind` has today — `document-type` | `agent-action` matched by
  string equality — and it is why three Epic 41 stories (41-19, 41-20, 41-21) could not express
  their "always escalates on \<payload condition\>" requirement and filed it back to 39-5 instead.
  Configuring such a class escalates *every* run of it. Where a payload predicate is genuinely
  needed the answer is one of three things that already work — make the state unrepresentable in
  the document type's validation, use the landed `BlockingReviewViolation` clamp, or route it as a
  typed side-effect edge — not a richer gate. A payload-predicate policy layer is a 39-5 change
  this epic deliberately does not attempt.
- `file_write` and `shell_execute` are single undifferentiated members and function as bypasses:
  `effect:git.pull-request.create` set to human-only is defeated by `git push` under
  `tool:git_operations.write`, and every governed route is reachable by `curl` under
  `tool:shell_execute`. Closing this needs a protected-path selector and a merged shell denylist.
- Production deploy is an **LLM tool loop**, not a typed activity — the pipeline dispatches generic
  `llm-call` with `enableTools=true`. Gating the deploy effect gates the *stage transition*; the
  deploy itself happens inside the loop. This must appear in the group description in the UI, not
  only in a doc.
- MCP is one coarse member with no drift signal. Adding a server, or a tool on an existing server,
  changes nothing in the catalog.

---

## Storage

Two tables, **control-plane resident in both modes**, and **deliberately excluded from the
destructive startup DROP list** — every other table on that list is operational data; these two are
the only thing between an agent and a production deploy, and the list runs on every restart without
`TAMMA_PRESERVE_DB=1`. The exclusion is tested.

CP residency is forced, not preferred. Tenant residency fails three ways: hosted services have no
ambient tenant context, the engine plane may carry no tenant at all, and — decisively — **a new
tenant migration never reaches already-provisioned tenants**; the tenant migrator is invoked only
from the two tenant-creation paths and there is no startup sweep.

- **`action_assignments`** — three scopes (platform / tenant / user), enforced by a CHECK; unique
  index with nulls-not-distinct; all three policy columns nullable; **no** CHECK on the threshold
  (a CHECK lives in a migration snapshot and would be a second permanent hardcoding of the dial
  bound).
- **`action_authorizations`** — the ledger that makes one human decision cover one deploy rather
  than one decision per retry.

**Principal resolution is mandatory, not optional.** The engine's service principal carries **no
user id**, and its tenant id comes only from a header and is nullable — so on the engine plane a
gate may have no resolvable principal, which breaks single-user resolution outright.

---

## Enforcement

Split exactly like `IAcceptanceRulesResolver`: a **pure evaluator + interface in Core** (Core cannot
touch a database — it has no project references), and the DB-backed implementation in `Tamma.Api`.

Named `AutonomyGate*`, **never `ActionGate*`** — `Tamma.Activities.Security.ActionGate` is a shipped,
DI-registered, constructor-injected type and the name collides inside `Tamma.Api`.

**Enforcement is live in v1, with defaults that reproduce today's behaviour exactly.** Every action
ships assigned as it behaves today; the admin opts into gating and it bites immediately. Shipping the
mechanism switched off, with a flip gated on a soak period, was considered and rejected — under it an
admin who sets deploy to human-only gets nothing.

Five seams:

| Seam | Where | Enforces |
|---|---|---|
| **A — llm-call** | the llm-call endpoint | **Never. Observe-only permanently.** |
| **B — tool dispatch** | one call site in the shared tool-loop path | Yes |
| **C — mutating routes** | an endpoint filter on the mediation routes | Yes |
| **D — background actors** | one call per tick per actor | Deny only |
| **E — Elsa graphs** | a gate activity, reaching the gate over HTTP | Yes |

**Seam A never blocks, in any version.** A requires-human returned there reaches a dispatch whose
calling workflow has no human route in 44 of 45 cases — escalation into a void — and blocking there
*and* at Seam E would double-gate deploy. Agent-action enforcement lives only at Seam E, where a real
human wait exists. Pinned by a test.

**Seam B sits after sanitization and before the parallel/sequential fork**, and is *not* nested
inside the optional validator block — placing it there would make the gate absent whenever the
validator is absent, and every dependency on that path is optional-nullable. The gate is a required
constructor parameter. The two existing fail-open allowlists stay; the gate is additive and cannot be
defeated by a null allowlist. A denial becomes a rejected-tool-call entry that the existing machinery
already turns into a message back to the model — no new plumbing, no exception. The outcome is named
`Denied`, not `RequiresHuman`: there is no human on that path and calling it escalation would be a
lie.

**Seam C is an endpoint filter, not an authorization handler** — tenant context is unset during
policy evaluation and there is no dynamic policy provider. Two security properties follow and are
worth keeping: the gate does **not** inherit the two unconditional superuser bypasses (a platform
admin can edit assignments but cannot bypass a governed effect), and it is unaffected by the
development-without-JWT blanket that re-registers every policy as anonymous.

**Denial returns `409`, never `202`.** The mediation client branches solely on success status, and
`202` is already a success code on that client — a 202 escalation would be indistinguishable from
success and the engine would proceed as if the effect had happened. `409` not `403`: the caller is
authorized; the *system* is not yet permitted to act on its own.

**Seam D can only deny.** A sweeper cannot suspend for a person, so `automation:*` descriptors are
marked non-escalatable, the API rejects a mid-range threshold on them, and the UI renders a two-state
control. Exceptions are caught inside the helper — a hosted service must never take down the host.

**Seam E reaches the gate over HTTP**, not by DI: the engine registers no repository and mediates
everything through the API client. v1 adopts it in one place — the deployment pipeline's production
approval decision — **by OR, never by replacement**. The existing business-mode predicate keeps
firing; a threshold-only replacement would be strictly weaker for business-mode tenants.

**Audit:** one event family, tags being the union of the two field sets the superseded specs
proposed. Emission is best-effort *except* denials under enforcement, which are not swallowed — a
block with no audit row is a compliance hole.

---

## The dial becomes one constant

`AutonomyDial` — min, max, default, and the `AlwaysHuman` sentinel, in one place, published over the
wire so the UI binds instead of hardcoding. The model carries **no lower bound**; the validated range
stays `[70,100]` for now, so widening downward later is a single edit.

Everything that currently hardcodes it: the domain validation, a UI constant plus slider minimum plus
helper text plus a test asserting the slider's minimum, a corrupt-row test vector that uses `5` (which
becomes legal at `[0,100]` and needs a new vector), two silent coverage loops that would leave a new
lower band unexercised, and the shipped routing-guidance prose that says "At the supervised baseline
(autonomy 70)…" — which reaches an agent, so it must stop asserting 70 as a permanent fact. Plus 13
documentation files.

**Two unlanded specs would each re-hardcode the bound** — Story 39-23's `minAutonomyLevel` and Story
42-1's `AutonomyFloor`. Both are corrected or superseded here; if either lands first the bound is
duplicated three ways.

---

## Epic 42 reconciliation

Epic 42 is structurally a second action catalog. None of it exists in code yet, so this is a spec
reconciliation, not a migration. This catalog is the single source of truth; Epic 42 consumes it.

| Epic 42 story | Verdict |
|---|---|
| **42-1** Tool contract & registry | Rewritten — drops the autonomy floor, permission class and category (all absorbed); keeps the secret requirement and the suspends flag |
| **42-2** Tool bindings store | **Deleted** — a third override store of identical shape; its two-scoping prose is transplanted |
| **42-3** Per-tool permission & autonomy gating | **Deleted** — its siting analysis and effective-ceiling insight are transplanted into Seam B |
| **42-5** Tool audit | Narrowed; loses its dependency on a result field documented as always empty |
| **42-6** MCP integration | Gains a catalog-binding prerequisite |
| **42-7 / 42-8A / 42-8B / 42-9** | Gating sections stripped; **42-8B's independently-binding escalation requirement deleted** — it would have meant two gates, two audit trails and two human decisions for one production deploy |

---

## Stories

Built through as one epic. The ordering below is sequencing, not separate releases.

| # | Title | Days |
|---|---|---|
| 0 | Prerequisite fixes and dead code | 2 |
| 1 | `AutonomyDial`: one constant, published, drift-tested | 2 |
| 2 | Catalog core: union vocabulary + fail-loud index | 5 |
| 3 | Groups: the 16-member partition + behaviour-preserving defaults | 3 |
| 4 | Tool-vocabulary reconciliation + startup validator | 3 |
| 5 | Storage, principal resolution, resolver, audit | 5 |
| 6 | Admin API + RBAC | 3 |
| 7 | Admin UI | 6 |
| 8 | Drift harnesses | 5 |
| 9 | The five seams, enforcement live, authorization ledger | 7 |
| 10 | Epic 42 spec reconciliation | 2 |

**Total ~43 days.** Stories 0 and 1 are independently valuable and ship first.

**Story 0** fixes a live bug worth landing on its own: the acceptance-rules edit dialog **built** its
save body without `acceptorRequirement`, and the API **defaulted** the missing field — so **every admin
save silently reset `design` from human-required back to `any`**. Also deletes a dead tool-resolution
activity with zero callers (a third dead tool vocabulary).

> **Status (2026-07-29): FIXED, on both sides.** The DTO field is now `AcceptorRequirement?`
> (`null` = "the caller did not say") and `AcceptanceRulesEndpoints.Upsert` resolves the in-force
> value before mapping, so an omitted field is preserved rather than invented; the dashboard sends
> and edits the field. The blast radius was three document types, not one — `sprint-plan` and
> `threat-model` also ship `AcceptorRequirement.Human` since 41-1b/41-1c. **The fix covers
> `PUT /api/acceptance-rules/{documentTypeKey}` only** — see carried defect **CD-1** below before
> treating the class as closed. *(Update 2026-07-30: CD-1 is now closed — the base route can no
> longer lower a shipped human acceptor floor.)*

### Carried defects and follow-ups

Defects found while implementing an Epic 43 story, deliberately NOT fixed in that story, and
therefore not owned by any story that has landed. **A follow-up recorded only inside the story that
deferred it is not tracked** — that is what this table is for.

| # | Defect | Found in | Owner | Status |
|---|---|---|---|---|
| **CD-1** | **Tier-2 wholesale shadowing erases the shipped human acceptor floor via the BASE route.** `AcceptanceRulesService.ResolveAsync` resolves **wholesale** — tier 1 per-type override row, tier 2 principal BASE override row, tier 3 `AcceptanceDefaults.For(type)` — with **no field merge**. So the moment a base override row exists it shadows tier 3 *entirely*. Consequence, proved: **one** `PUT /api/acceptance-rules/base` writes a base row carrying that row's in-force `acceptorRequirement` (`any`), and from then on `design`, `sprint-plan` **and** `threat-model` all resolve to `any` — their human floor gone, without any of them having been written — and `threat-model` additionally loses its `security` reviewer selection. Worse, a **later omitting per-type save** then reads the degraded value as "what is in force" and bakes it into a type row, after which deleting the base row no longer restores the floor. **This is a resolution-semantics issue, not an omission bug:** it fires on an *explicitly stated* `acceptorRequirement` exactly as it does on an omitted one, so 43-0's preserve-on-absent cannot close it. **Not UI-reachable today** — the admin page renders only the ten per-type rows and issues no base PUT — but reachable by anything that speaks HTTP. Semantics are 39-5 D1/D2's and predate 43-0. **The follow-up must decide** whether tier 2 stays wholesale (and the base route grows a guard REFUSING to lower a floor below any shipped per-type floor) or tier 2 becomes a per-field merge for `AcceptorRequirement`. The two are not equivalent and the choice is a product one. | Story 43-0 adversarial review, 2026-07-29 (recorded as that story's amendment **A1**) | **closed 2026-07-30** — see 43-0 amendment **A1 → "RESOLUTION — 2026-07-30"** | **CLOSED (with one recorded remainder).** **Decision: tier 2 stays WHOLESALE; the shipped per-type `AcceptorRequirement` becomes a FLOOR** — a tier may raise it, never silently lower it. The general per-field merge was evaluated against 39-5 D2 and REJECTED in its own words ("field-level deep-merging makes provenance unexplainable in the admin UI and has no precedent"); the floor changes exactly ONE field, composing it by `max()` over the two-element lattice `any < human`, and leaves wholesale-row precedence intact for everything else, so `source` still names the row that produced the resolution. **Tier 1 is deliberately exempt**: a per-type `PUT` stating `"acceptorRequirement": "any"` still lowers it — lowering a shipped human floor must NAME the type, which is the semantic `Upsert_explicit_any_clears_the_human_floor` already pinned. A base row stands in for every document type at once and cannot express intent about any one of them. The **bake-in path closes as a consequence**: 43-0's preserve-on-absent now reads the floored value, so an omitting per-type save writes `human` and deleting the base row still restores everything. Corroborating evidence for the floor framing: `ActionCatalog.Descriptors` already pins `document-type:design|sprint-plan|threat-model` at `AutonomyDial.AlwaysHuman` *because* those types ship `AcceptorRequirement.Human`, so the pre-fix behaviour had the catalog and the resolver openly disagreeing. Shipped as `Tamma.Core/Documents/Policy/AcceptanceFloors.cs` + `ResolvedAcceptanceRules.AcceptorRequirementFloored` (additive on the wire, carried into the dashboard interface); pinned by `AcceptanceFloorsTests` and by `Upsert_base_cannot_erase_the_shipped_human_acceptor_floor` (which fires on an EXPLICIT `any`, proving it is resolution semantics and not 43-0's omission bug). **Recorded remainder, deliberate:** `threat-model`'s `security` reviewer selection is STILL shadowed wholesale by a base row — reviewer roles carry no ordering, so no monotone floor exists for that field, and a deployment-wide reviewer choice is a legitimate thing for tier 2 to say. Pinned as intentional by `Base_row_still_shadows_per_type_reviewer_selection_by_design`. |

**Also closed on 2026-07-30, recorded inside Story 43-5 rather than here** (listed for
discoverability, since the table's own preamble warns that a story-local follow-up is untracked):
**F6** — the autonomy gate failed OPEN on a degraded read (a cold policy snapshot, or a base-rules
read that threw, silently discarded the legacy always-escalate floor). It now fails **CLOSED**, with
"read failed" made structurally distinct from "read succeeded and found nothing"
(`GovernancePolicySnapshot.IsAuthoritative`, a nullable `baseRules` meaning *unreadable*,
`ActionAssignmentSource.Unavailable`, a `degraded` audit tag, ERROR-level logging). **F10** — cross-plane
`Enforce` and `AllowedRoles` composed non-monotonely, so a platform ceiling could LOOSEN; they now
compose by `OR` and by INTERSECTION respectively, making every cross-plane field monotone. See
`story-43-5/43-5-storage-principal-resolution-resolver-audit.md` → "F6 — CLOSED" / "F10 — CLOSED".

**⛔ OPENED by the F6 close, and BLOCKING Story 43-9 — `43-5 F11`: there is no break-glass override
for the fail-closed posture.** A non-authoritative governance snapshot now DENIES every catalogued
tool, in both SaaS and single-user-with-a-control-plane deployments, and no config flag, env var or
admin endpoint can force the gate open. It self-heals within the 60 s refresh TTL and logs loudly at
ERROR, so this is a *recovery-lever* gap and not a diagnosis one — but 43-9 carries the same posture
into four more seams with larger blast radius. **Deliberately not built:** the knob's shape (who may
set it, whether it auto-expires, how each use is audited) is a product decision. Recorded in full
under 43-5 → "Open follow-ups" → **F11**, and cross-listed in 43-9's Dependencies. Alongside it,
**`43-5 F12`** records that the one live seam HARD-DENIES rather than escalating —
`ToolLoopGateOutcome` has no `RequiresHuman` case, so a degraded decision reaches the model as a
tool rejection and reaches no person at all.

> **Correction (2026-07-25).** An earlier draft of this line also asked Story 0 to "resolve
> `GetAcceptanceRulesTool` — DI-register or delete, it must not stay a tool the registry cannot
> see." That was wrong. It is deliberately not registered as an `IToolExecutor`
> (`Program.cs:411-417`, Story 39-5 Design Decision D6) — the factory mints principal-bound
> instances per tenant-agent session, so a singleton registration would be the bug. Story 4's
> startup validator gives it a permanent allowlist entry instead of "fixing" it.

**Story 7 is the least reliable estimate in the plan** — three React primitives with no in-repo
precedent (a row-level toggle, a grouped table, and a dimmed row with a why-disabled tooltip). The
dimmed-row treatment exists in the repo, but in Blazor.

---

## Decisions

| | Decision | Rejected |
|---|---|---|
| **D1** | v1 **enforces**, with defaults reproducing today's behaviour. Admins opt into gating. | Declarative v1; enforce-with-pre-gated-defaults (two behaviour changes in one release) |
| **D2** | Unclassified action is **allowed at runtime, unmergeable in CI**. | Fail-closed at runtime (any gap becomes a production stall); no guard at all |
| **D3** | Model carries **no lower bound**; `[70,100]` stays as one named constant. | Widening now; keeping 70 permanently (which would make the greyed rows pointless) |
| **S1** | Union catalog across all namespaces | Agent-actions only |
| **S2** | Single source of truth; Epic 42 consumes | Two peer governance models |
| **S3** | Level-independent storage, level-parameterized display | Explicit level×action matrix |
| **S5** | Groups assignable as a whole; individual actions override | Group-only |
| **S6** | Both operating modes | — |

---

## Open questions

1. **MCP server trust and tenancy** — may a tenant admin register a tenant-scoped MCP server, or is
   the allowlist platform-owned? Determines whether MCP invocation can ever be finer-grained than one
   member. Not derivable from code.
2. ~~**Is secret-reveal gateable at all?**~~ — **ANSWERED (2026-07-25): NO. Reading a secret never
   requires a human.**

   `effect:secret.reveal` is **removed from the gateable set**. It is not an approval checkpoint —
   it is the mechanism by which a tool or provider call gets the credential it was already
   authorized to use, and it can fire many times inside a single agent run. Gating it would mean a
   human approval per credential fetch, which is nonsense at best and an outage amplifier at worst.

   What governs a secret is therefore the **action that needs it**, not the read: if deploying is
   gated, the deploy is what waits for a person — the credential fetch inside it is not a second
   gate. The `secrets` group keeps its other members (rotation, the automation actors); the reveal
   effect is catalogued as **informational only, never enforceable**, and 43-2 must model that as a
   descriptor property rather than leaving it as a threshold an admin could accidentally raise.

   *(Audit is a separate matter and unaffected: `ISecretAccessAuditor` should still record reveals.
   Its only implementation is a null one today — tracked in the Epic 42 notes, not here.)*

3. **Compliance framing.** This epic keeps `SensitiveActionCatalog` as the compliance artifact and
   the action catalog as the authorization artifact, joined by one optional field. If legal needs one
   artifact with SOC2 mappings across all ~153 members, that is materially larger scope and must be
   settled before the descriptor shape freezes.
4. ~~**Should changing an assignment require two people?**~~ — **ANSWERED (2026-07-25): no.**

   And the answer came with a scoping correction that matters more than the question. **The action
   list and the automation toggle are different layers, at different scopes:**

   | Layer | What it is | Scope | Who changes it |
   |---|---|---|---|
   | **The catalog** — actions, groups, risk, descriptors | the *vocabulary*: what actions exist | **PLATFORM** | ships in code; changed by a release, never by an admin |
   | **The platform ceiling** — platform-scope assignment rows | floors a tenant cannot go below | **PLATFORM** | platform owner |
   | **The automation toggle** — per-action / per-group thresholds | automated vs human, per level | **TENANT** | tenant admin, like any other tenant setting |

   So a tenant admin never adds, removes or renames an action — they only decide, within your
   ceiling, which of the platform's actions their agents may do unattended. One admin, one write,
   recorded in the audit event. No two-person mechanism is built.

   This is what the model in §1–§4 already does — the catalog is a code-resident `[Wire]` vocabulary
   and only `action_assignments` is per-principal — but an earlier draft of this answer said "the
   action list is tenant-level configuration", which was wrong and would have licensed a
   tenant-editable catalog. Corrected.

   Consequence: **the platform ceiling is now the load-bearing protection**, not a nicety. It is the
   only thing standing between a tenant admin and full automation of a destructive action, so the
   `max()` composition in §4 is a safety mechanism rather than a convenience, and its tests should be
   read that way.

   The self-grantable-permission observation stands but is **out of scope here**: API key
   permissions are accepted free-form with no validation against `Permissions.Matrix`
   (`AdminApiKeysEndpoints.cs:63`). That is a pre-existing auth-plane gap affecting every
   permission, not something this epic introduces or should fix.
5. **Is draining the ungoverned-route backlog scheduled anywhere?** The ratchet guarantees it only
   shrinks when the work is actually done — and no story yet schedules that work.
