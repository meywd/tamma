# Implementation Plan — Story 43-11: The Automation-Level Model, 1–100

## Scope & Deliverable

When this story is done, `AutonomyDial.Min` is `1`, every one of the 197 catalogued actions carries an
explicitly-chosen level in `[1,100]` (no helper default remains), an action at or below the deployment's
current level is automated and **not individually switchable**, an action above it carries a one-row
per-action toggle, and a test proves that the set of automated actions at level 70 is a **strict** subset
of the set at level 100 — which is false today for all 197 rows.

**The code is small. The deliverable is the level table and the migration decision table**, and both need
review time out of proportion to the estimate. The same warning `43-3`'s plan carries applies here for the
same reason: an assignment table is cheap to write and expensive to get wrong, and these numbers become
the behaviour of a running system.

## Pre-Reading

- `docs/stories/epic-43/story-43-11/43-11-automation-level-model-and-per-action-levels.md` — ACs are source of truth; §M1 is the rule, §M2 the table, §M5 the migration
- `docs/stories/epic-43/README.md:549` — decision **D3** ("model carries no lower bound; widening IS editing `Min`") and its rejected column ("keeping 70 permanently, which would make the greyed rows pointless")
- `docs/stories/epic-43/story-43-1/43-1-autonomy-dial-one-constant.md` — AC1/AC2/AC4–AC8/AC12; **note the Status line: AC2 and AC4–AC8 have not landed** and this story lands them
- `docs/stories/epic-43/story-43-3/43-3-groups-and-behaviour-preserving-defaults.md` — D4 (the AlwaysHuman derivation this story retires), D5 (the four contested group assignments the level rule inherits)
- `docs/stories/epic-43/story-43-5/43-5-storage-principal-resolution-resolver-audit.md` — AC1–AC3 (the storage, and the deliberate absence of a numeric CHECK), AC8 (the `max()` / `??` ladder)
- `docs/stories/epic-43/story-43-7/43-7-admin-ui.md:110-121` (the contract at `:115-117`) — **the greyed-row contract this story overturns**; read it before touching the dashboard
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs` — all 53 lines
- `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs:38-76` — the six helpers; `:241,253,255,388` — the four sentinels
- `apps/tamma-elsa/src/Tamma.Core/Actions/ActionGroup.cs:41-87,105-163` — the 16 groups and their UI descriptions (the containment predicate reads the first, the UI copy the second)
- `apps/tamma-elsa/src/Tamma.Core/Actions/ActionRisk.cs:20-29`, `ActionDescriptor.cs:15,18-26`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceDefaults.cs:33,120-172,206-223` — the three human-acceptor rows and the `For` switch
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceFloors.cs:57-90` — `Max` (`:65`), `ShippedFloorFor` (`:69-70`), `ApplyShippedAcceptorFloor` (`:80-85`)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs:98-181` (the policy view), `:187-200` (the threshold PUT), `:569-625` (the two mid-range rejections)
- `apps/tamma-elsa/tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs` — the whole fixture; most of it moves
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Actions/ActionEnforcementSitesTests.cs:159-176` — the 21-of-197 honesty pin, which must survive untouched

## Corrections to the model as stated

- **C1 — "removing the 101 sentinel" cannot mean deleting `AutonomyDial.AlwaysHuman`.** `grep -rn AlwaysHuman --include=*.cs tests` returns **129 hits across 17 files**, and `src` uses it for three things that survive the widen: `ActionCatalog.UnclassifiedFallback` (`ActionCatalog.cs:49`), the fail-closed substitution for an unreadable policy input (`AutonomyGateEvaluator.cs:286,530`), and the legacy always-escalate floor (`:301-307`). Deleting the constant would rewrite the whole fail-closed posture 43-5 closed under F6. **What is removed is its use as a shipped descriptor default** — four rows. Story §M6 states this; the plan repeats it because "remove the 101 sentinel" reads like a delete.

- **C2 — `AcceptanceDefaults.DefaultAutonomyLevel` and `AutonomyDial.Min` are only incidentally equal today, and `AutonomyGateEvaluatorTests` depends on it.** `EmptyTable_AtShippedDial_OutcomeMatchesShippedBehaviour` opens with the comment "*With the shipped dial (`AcceptanceDefaults.DefaultAutonomyLevel == AutonomyDial.Min`) …*". That premise becomes false. The test's *logic* is comparison-driven and survives, but its else-branch now fires for the 22 upward moves — walk it, do not assume it stays green.

- **C3 — `automation:*` cannot be given real levels without an API change.** `ActionPolicyEndpoints.cs:614-624` rejects any mid-range threshold on a non-escalatable target, and `:569-598` does the same for group writes. All 29 `automation:*` members are non-escalatable (`ActionDescriptorMetadataTests.cs:44-53`). The rejection was correct under a two-value dial and is wrong under 1–100: the right fix is to keep the *semantics* (below the level a sweeper is **denied**, never escalated) and change the *wording*, not to keep refusing the write.

- **C4 — there is no migration to write.** 43-5 deliberately left no numeric CHECK on `min_autonomy` (`20260729070256_AddActionGovernance.cs:32-33`). Do not add one "for safety" — that is precisely 43-1 AC12's forbidden second bound.

## Design Decisions

- **D1 — Containment is derived from `ActionGroup`, with four named exception lists, and the exceptions are written into the story rather than into code.** The level table is a static array; the derivation exists so a reviewer can argue with a number, not so the code can recompute one. Do **not** implement `Containment` as a C# enum or a computed property: that would be a second vocabulary over the catalog (the thing 43-2 D1 rejected a flat 153-member enum for), and it would silently reclassify actions when a group changes. The rule lives in comments and the story; the levels live in the descriptor table.

- **D2 — The level is written as a plain integer literal in the descriptor table, not as an `AutonomyDial.*` constant.** This is a deliberate reversal of 43-3's "never literals" rule, and the reason that rule existed is gone: it existed because 193 rows all meant *the same thing* — "the floor, whatever the floor is" — and a literal would not have moved with the dial. A level of `65` does not mean "the floor"; it means sixty-five, and it must **not** move when `Min` moves. 43-1 AC10's drift guard is comparison-shaped and explicitly not a bare-literal scan (`43-1…md:114`), so 197 integer literals do not trip it. Add a comment at `ActionCatalog.Descriptors.cs`'s header recording this reversal, or someone will "fix" it back.

- **D3 — `AcceptorRequirement` becomes derived, and `AcceptanceFloors` keeps its shape.** The narrowest change that stops the catalog and the acceptance resolver disagreeing is to leave the `max()` lattice (`AcceptanceFloors.cs:65`) and the tier-1 exemption exactly as CD-1 closed them, and move only the *input*: `ShippedFloorFor(type)` reads `ActionCatalog.Get(new ActionKey(DocumentType, type.ToWire())).DefaultMinAutonomy` against the resolved dial. Everything CD-1 protected still holds — a base `PUT` cannot lower the floor, a per-type `PUT` naming the type still can. **Do not** instead delete `AcceptanceFloors`: the wholesale-shadowing defect it closes is unrelated to the dial and would reopen.

- **D4 — Level ownership is enforced at the API, not in the evaluator.** `AutonomyGateEvaluator` stays pure and stays a `max()`/`??` ladder; it must keep resolving an action row that already exists, whatever the dial now says, or a dial *raise* would retroactively invalidate stored rows and change decisions without a write. Ownership is a **write-time** rule (409 on `PUT`), plus a read-time flag (`levelOwned`) for the UI. Consequence, recorded rather than designed away: a row written when an action was above the level survives a dial raise that makes the action level-owned. It is then redundant (`min = oldDial ≤ newDial`) and harmless, and the UI shows it as an `action-override` on a dimmed row. A sweep that deletes newly-redundant rows on dial change is **not** in this story — silent deletes on a governance table are worse than a redundant row.

- **D5 — The AC9 decision table is test-resident and shrink-only.** Copy `ContractBindingTests`' `KnownContractViolations` ratchet exactly: an entry may be removed (the action was rebased), a stale entry naming an action that no longer moves up **fails the build**, and adding an entry requires a reason string. This is the mechanism that stops the 22-row list rotting as seams land in 43-9.

- **D6 — Land the levels and the API/UI in one commit, not two.** A commit that assigns levels while `ActionPolicyEndpoints.cs:148` still returns `editable = true` ships an admin surface that lets a tenant admin lower a level-owned action — a governance regression that exists only between the two commits. If the work must be split, split it the other way: enforcement first (409 with the *old* uniform levels is a no-op), levels second.

- **D7 — Do not touch `ActionVocabularyCountTests` or `ActionEnforcementSitesTests`.** If either goes red, the story has added or removed an action, or claimed an enforcement site that does not exist. Treat a red there as a stop-work signal, not a pin to bump.

## Implementation Steps

1. **Review-first pass — produce the level table as a reviewable document before touching code.** Walk all 197 descriptors against §M1's rule. For each, record containment class, risk/reversibility cell, resulting level, and — for every action where the rule's output feels wrong — the disagreement, in the PR description. Independently re-derive the four exception lists in the I2/I3 predicates; do not take the story's word for them. **This step is the story.** Output: the §M2 table, plus every disagreement, in the PR body.

2. **Produce the AC9 migration decision table.** Re-derive the 22 upward moves from your own table (not by copying §M5), then re-derive which are live by grepping `.EnforcesGovernance()` in `Program.cs`, `BackgroundActionGateAccessor` in `src/Tamma.Api/Services/`, and the tool-loop seam in `InlineToolLoopRunner.cs`. Get an explicit `ACCEPT`/`REBASE` per row from the product owner **before step 4**. `tool:shell_execute` (OQ1) is the one that decides whether the whole story is shippable at a default dial of 70.

3. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AutonomyDial.cs`** — `:27` `Min = 70` → `1`. Update the XML doc at `:5-24` so "the supervised baseline" no longer describes `Min` and the D3 claim reads as *performed* rather than *available*. `AlwaysHuman`, `IsValidThreshold` and `ValidLevels` are unchanged (C1). One line of behaviour, several of prose.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Actions/ActionCatalog.Descriptors.cs`** — six helper signatures (`:38-76`) take a required `int level`; the four `min: AutonomyDial.AlwaysHuman` (`:241,253,255,388`) become `level: 80 | 85 | 90 | 80`; all 197 call sites gain a literal level (D2). Add the header comment recording D2's reversal of 43-3's "never literals". Keep the existing per-descriptor rationale comments — they are still true about *grouping*, and several now also explain the level.

5. **MODIFY `ActionDescriptor.cs:18-26`** — the `DefaultMinAutonomy` doc still says the range is `[Min, AlwaysHuman]` and that "the only `AlwaysHuman` member is `document-type:design`". Rewrite: the range is `[Min, Max]`, `AlwaysHuman` is not a legal descriptor value, and the level means "automated iff `dial >= level`".

6. **MODIFY `AcceptanceRules.cs:85-86`** (Story 43-1 AC2) — `!AutonomyDial.IsValidLevel(AutonomyLevel)`, message interpolated from the constants. Then `AcceptanceRules.cs:30`'s XML doc loses its numbers.

7. **MODIFY `AcceptanceDefaults.cs` + `AcceptanceFloors.cs`** (D3) — drop `AcceptorRequirement = AcceptorRequirement.Human` at `:122,146,170`; `ShippedFloorFor` (`AcceptanceFloors.cs:69-70`) derives from the catalog level and the resolved dial. `AcceptanceDefaults.cs:33`'s `DefaultAutonomyLevel = 70` is **untouched**. Expect `AcceptanceDefaultsDriftTests`, `AcceptanceFloorsTests` and `AcceptanceRulesEndpointsTests.Upsert_explicit_any_clears_the_human_floor` to need re-vectoring, not deletion.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/ActionPolicyEndpoints.cs`** — the policy view (`:98-181`) gains `shippedLevel`, `levelOwned`, `reason`, and `editable` becomes `!levelOwned` (delete the S3 comment at `:145-147` and cite this story instead); `PutActionThreshold` (`:187-200`) gains the 409/400 rules of AC8; `ValidateThresholdForAction` (`:600-625`) and `InvalidGroupThreshold` (`:569-598`) stop rejecting mid-range on non-escalatable targets and instead carry the deny-not-escalate wording into the response (C3).

9. **REWRITE `tests/Tamma.Core.Tests/Actions/ActionCatalogDefaultsTests.cs`** — delete `ShippedAlwaysHuman` (`:29-70`) and `EveryOtherMember_DefaultsToMin` (`:83-91`); invert `ShippedDefaults_ReproduceTodaysGatingBehaviour` (`:72-81`) to assert the `AlwaysHuman` set is empty; rewrite `DesignDocumentType_MatchesAcceptanceDefaults` (`:93-120`) as the biconditional over every type × every valid level; rename `Deploy_ShipsAtMin_PerEpicDecisionD1` (`:122-141`), `McpToolInvoke_ShipsAlwaysHuman_BecauseTheCiHalfCannotExist` (`:153-166`) and `TriageIntake_ShipsAtMin_FloorComesFromAlwaysEscalate` (`:168-178`) to pin their new levels **with the old reasoning preserved as comments** — all three carry decisions that were argued and must not look like they were forgotten (43-3 D7's point about `triage-intake` is unchanged: the floor still comes from the legacy surface, only the catalog number moved); keep `UnclassifiedFallback_is_AlwaysHuman` (`:189-193`) unchanged with a new comment.

10. **ADD `tests/Tamma.Core.Tests/Actions/ActionCatalogLevelTests.cs`** — the 197-row `(key → level)` table (AC4) with a symmetric-difference message, plus AC5's four strict-subset/monotonicity assertions, plus AC3's distribution guard, plus AC9's shrink-only decision table.

11. **RE-VECTOR the three silent coverage sites** (AC14, Story 43-1 AC6/AC7) — `AcceptanceContractTests.cs:98`, `AcceptanceGuardrailsTests.cs:186`, `AcceptanceRulesServiceTests.cs:104-117`. **Do this before running the suite**, not after: without it the new 1–69 band ships unexercised and the corrupt-row test goes quietly vacuous, and both failure modes are invisible in a green run.

12. **MODIFY `AcceptanceRulesModelTests.cs:22-28`** — `[TestCaseSource]` over the constants, renamed off `_70_to_100` (Story 43-1 AC8). This test **will go red** at step 3 and that is the signal the widen took effect.

13. **MODIFY the dashboard** — `RulesEditDialog.tsx:35-36` (delete the constants), `:183` (helper text), `:188-189` (slider bounds) all bind to `GET /api/actions/dial`; `AcceptanceRulesAdminPage.test.tsx:116-123` sources both bounds from the mocked payload. Then the action-catalog page's dimmed-row behaviour per AC13 — coordinate with Story 43-7's state before writing anything (it may not exist yet; if not, fold AC13 into 43-7 and say so in both stories).

14. **AMEND the epic docs** (AC15) — `README.md:549`, `story-43-1/…:131`, `story-43-3/…` (the AlwaysHuman derivation), `story-43-6/…` AC7, `story-43-7/…` the greyed-row contract. Each gets a dated amendment pointing here, never a silent rewrite.

15. **Run `dotnet test` and `dotnet ef migrations has-pending-model-changes`.** The second is expected clean and trivially so; if it is not, step 4 or 8 touched an entity it should not have.

## Risks

| Risk | Mitigation |
|---|---|
| **`tool:shell_execute` at 80 breaks every autonomous run at the shipped dial of 70.** Seam B is live. | Step 2 gets an explicit product decision before any code lands. If the answer is `ACCEPT`, the release note must say the working dial is now ≥ 80. |
| **Seam D denies rather than escalates**, so `outbox-slack-sender`/`outbox-smtp-sender` at 75 silently stop draining queued notifications at dial 70. | Same decision table; and AC11 requires the API and UI to say "denied", so nobody reads the row as "waiting for a person". |
| **The level table looks mechanical and gets rubber-stamped.** | Step 1 requires the disagreements in the PR body, and OQ1–OQ8 name the eight the author already found. A review that adds none is a review that did not happen. |
| **Someone "fixes" the 197 integer literals back into `AutonomyDial.*` constants** (D2). | Header comment at the descriptor table plus the AC3 distribution guard, which goes red the moment they collapse to one value. |
| **`AutonomyDial.AlwaysHuman` gets deleted** on a reading of "remove the 101 sentinel" (C1). | The three surviving jobs are named in §M6, in the AC6 comment, and here. `UnclassifiedFallback_is_AlwaysHuman` stays green as the tripwire. |
| **A dial raise retroactively strands per-action toggle rows** (D4). | Recorded, not swept. The row is redundant and harmless; a silent delete on a governance table is the worse failure. |
