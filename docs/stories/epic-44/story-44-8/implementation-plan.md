# Implementation Plan — Story 44-8: External Link — GitHub Import, `ExternalRef`, Opt-In Outbound

## Scope & Deliverable

When this story is done a team can import an existing GitHub backlog into a project in one call, work items carry a typed, indexed link back to their origin issue, a work item can be linked or unlinked by hand, and — opt-in per project, GitHub-only — a terminal status change posts a comment and optionally moves one label on the linked issue. What it deliberately does **not** do is sync: no webhook consumption, no reconciliation, no conflict model, no write-back of title, body, state or assignee, and no refresh of the captured external state. Divergence is shown, not resolved.

## Pre-Reading

- `docs/stories/epic-44/README.md` — the whole "external-platform relationship" section and Decisions **D3**; this plan implements exactly the narrow slice argued there
- `docs/stories/epic-44/story-44-1/implementation-plan.md` — D7 (`ExternalRefJson` left shapeless for this story), the partial index, `CreateManyAsync`
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/IGitPlatformClient.cs:29-119` — **all twelve methods**, so the absence of issue CRUD is verified first-hand, not taken on trust
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/Models/Issue.cs:4-13` — the dead record and its deferred-fields note
- `apps/tamma-elsa/src/Tamma.Platforms.Abstractions/PlatformKind.cs:12-26` — six members, no plain Git, and the "drivers land in 31-11 / 31-12" comment
- `apps/tamma-elsa/src/Tamma.Api/Services/Engine/IGitHubEngineCallbackService.cs:38-79` — `ListIssuesAsync:45`, `PostIssueCommentAsync:54`, `AddIssueLabelsAsync:58`, `RemoveIssueLabelAsync:62`, `IssueListResult:76`
- `apps/tamma-elsa/src/Tamma.Api/Services/Engine/OctokitGitHubEngineCallbackService.cs:246,257,265` — the Octokit calls behind them
- `apps/tamma-elsa/src/Tamma.Activities/ADL/ApplyTriageResultActivity.cs:229-263` — the shipped write-back pattern **and** `EnsureSuccess`'s status-code-only, body-free error rule at `:257-263`
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/TriageDecision.cs:23-31,:52-109` — `TriageIssueType` and the alias-folding parser used for label inference
- `apps/tamma-elsa/src/Tamma.Data/Entities/TenantPlatformInstallation.cs:35-145` + `Tamma.Api/Services/Platforms/SecretStorePlatformCredentialReader.cs:35,60` — per-tenant credential resolution
- `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitTokenResolver.cs:28` — the hardcoded `"github"` that makes multi-platform a larger job than it looks
- **All referenced paths exist.** NOT FOUND (this story creates them): `Tamma.Core/Tracking/ExternalRef.cs`, `Tamma.Api/Services/Tracker/{IssueImportService,OutboundLinkService}.cs`.

## Design Decisions

- **D1 — Import reads `IssueListResult`'s raw `JsonElement`s, not the `Issue` record.** `Models/Issue.cs:7` is never returned by any interface method and never constructed in `src/`; adopting it here would be resurrecting dead code, and it would still need mapping (`string Number`, no assignee, no timestamps). `IGitHubEngineCallbackService.ListIssuesAsync:45` returns `IssueListResult(IReadOnlyList<JsonElement> Issues, int Total)` (`:76`) — a pass-through of the Octokit payload — so the mapper reads the fields it needs and ignores the rest, which is also what makes it tolerant of GitHub adding fields.

- **D2 — GitHub-only, and a non-GitHub platform gets a named rejection rather than a partial attempt.** Gitea and GitLab have drivers but no issue-read method anywhere (`IGitPlatformClient` has none, and the engine-callback service is GitHub/Octokit by construction). Bitbucket and Azure DevOps have no class at all. `GitTokenResolver.cs:28` hardcodes `"github"`. Attempting a "best effort" multi-platform import would mean adding issue methods to the abstraction and two drivers — the work D3 explicitly declines. So: `PLATFORM_IMPORT_UNSUPPORTED`, naming the platform, stating GitHub-only. An honest 400 beats a silent empty import.

- **D3 — Import is a snapshot and says so on the wire; re-running skips, never updates.** The `(repoFullName, number)` partial index (44-1 D7) makes the skip a cheap lookup. Updating a linked work item from the external issue on re-import would be a one-way sync with all the questions a sync has — what if the title was edited locally? what about status? — and no answers. Every entry returns `imported | skipped-already-linked | skipped-linked-elsewhere | failed`, matching the outcome-list shape 44-3 D8 established for the apply seams.

- **D4 — Import is bulk, using `CreateManyAsync`, not a loop.** 44-1 D6 mints keys under a `FOR UPDATE` row lock; looping `CreateAsync` over 500 issues takes 500 sequential locks and is exactly the pathology 44-1's Risks section flagged. `CreateManyAsync` takes the lock once and allocates a block. Test 4 imports 500 and asserts contiguous keys plus a bounded statement count.

- **D5 — Kind and priority are inferred from labels through the *shipped* vocabularies, not a new mapping table.** `TriageIssueType` (`bug|feature|chore|question|security|docs`) and `TriageVocabulary.TryParsePriority` (which folds `critical`→urgent and `medium`→normal, `TriageDecision.cs:64-66`) already exist, are alias-aware and are count-pinned. A label matching an issue-type wire sets `IssueType`; a `priority-*` label sets `Priority`; a `bug` label additionally sets `Kind = bug`. Unmatched labels are preserved verbatim in the work item's description footer rather than dropped, so no information is lost by an import.

- **D6 — Outbound is opt-in per project, three-valued, default off, and reaches only the two operations that already ship.** `projects.OutboundMode ∈ {off, comment, comment-and-label}`. `comment` posts on transition to a terminal status; `comment-and-label` additionally adds/removes one configured label. **Nothing else** — no title, body, state, assignee or milestone, because `IGitMediationService.UpdateIssueRequest.Status` is itself commented "today: no state change" (`GitRequests.cs:71`) and inventing a state mapping across GitHub's binary open/closed and seven tracker statuses is a sync decision in disguise. Test 8 asserts no other platform method is invoked.

- **D7 — Outbound failure never fails the tracker mutation, and the error carries no response body.** The tracker is the source of truth; a GitHub 502 must not prevent a card moving. Caught, emits `WORKITEM.OUTBOUND.FAILED` with the numeric status code only — the `ApplyTriageResultActivity.EnsureSuccess` rule (`:257-263`: "status-code only (no response body) to keep secrets out of the event/log"). This is the **opposite** posture to `ApplyTriageResultActivity`'s own fail-loud choice, deliberately: there, the write-back *is* the workflow's purpose; here it is a courtesy on top of a mutation that has already succeeded.

- **D8 — `lastKnownExternalState` is captured once and never refreshed.** Title, state and labels as of import/link, with the timestamp. Refreshing it — on a schedule, on read, or on webhook — is the first step of a sync, and a half-sync with no conflict model is worse than none because it looks authoritative. The UI shows the capture time; a user who wants current state clicks the link. AC9's test asserts this story registers **no** `IHostedService` and **no** `IWebhookHandler`, which is the mechanical guard against the feature growing by accident.

- **D9 — One external issue links to at most one work item per tenant, enforced with a `409`.** Two work items claiming the same origin makes outbound ambiguous (which one's status change posts the comment?) and makes the import skip-check meaningless. Enforced by a partial unique index on the `ExternalRefJson` expression, so concurrent links cannot both win.

- **D10 — No `IWebhookHandler` is written, even though writing one would be easy.** There are **zero** production `IWebhookHandler` implementations in the repo; issue webhooks are classified (`DefaultWebhookEventCategoryMapper.cs:30,42,53`) and dispatched to nothing. Writing the first one here would make the tracker the reason the platform's webhook plane went live, with the tracker's needs shaping a platform seam. That is a platform story with its own design; this story stays a consumer of what exists.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Core/Tracking/ExternalRef.cs`** — the record of AC1 plus `LastKnownExternalState(string? Title, string? State, IReadOnlyList<string> Labels, DateTimeOffset CapturedAt)`, `[JsonPropertyName]`d, with a canonical `(repoFullName, number)` normalization (lower-cased repo, trimmed) used by both the index and the lookup.

2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Tracker/IssueImportService.cs`** — D1–D5. Resolves the installation, rejects non-GitHub (D2), paginates `ListIssuesAsync`, maps, dedupes against the index, calls `CreateManyAsync`, returns the outcome list.

3. **CREATE `.../Tracker/IssueLabelMapper.cs`** — D5's inference, pure and unit-testable, delegating to `TriageVocabulary`.

4. **CREATE `.../Tracker/OutboundLinkService.cs`** — D6/D7. Invoked by `TrackerService` after a committed status change; reads `projects.OutboundMode`; calls `PostIssueCommentAsync` / `AddIssueLabelsAsync` / `RemoveIssueLabelAsync`; catches everything and emits `WORKITEM.OUTBOUND.FAILED`.

5. **MODIFY `Tamma.Data/Repositories/WorkItemRepository.cs`** — `FindByExternalRefAsync(repoFullName, number)`, `SetExternalRefAsync`, `ClearExternalRefAsync`, `CreateManyAsync` (if 44-1 shipped it as a stub, complete it here).

6. **MODIFY `Tamma.Api/Endpoints/TrackerEndpoints.cs`** — `Import`, `Link`, `Unlink`. `Import` and outbound configuration require `TrackerManage`; `Link`/`Unlink` require `TrackerView`.

7. **MODIFY `Program.cs`** — three routes in the 44-2 group; `AddScoped` the two services and the mapper.

8. **MODIFY `Tamma.Api/Services/Tracker/TrackerService.cs`** — call `OutboundLinkService` after a committed terminal status change, fire-and-forget-with-logging (D7).

9. **MODIFY `TrackerActionDescriptors.cs`** — `effect:work-item.import`, `.link`, `.unlink`, `.outbound-comment`, `.outbound-label`, all with a higher `DefaultMinAutonomy` than intra-tracker mutations: they reach an external system.

10. **CREATE tests** under `apps/tamma-elsa/tests/Tamma.Api.Tests/Tracker/`.

## Data & Migrations

**Two additions to `projects`:** `OutboundMode text NOT NULL DEFAULT 'off'` with `ck_projects_outbound_mode`, and `OutboundLabel text NULL`.
**One index on `work_items`:** a partial **unique** index on `(ExternalRefJson->>'repoFullName', ExternalRefJson->>'number') WHERE "ExternalRefJson" IS NOT NULL` (D9) — 44-1 created a non-unique partial index; this promotes it.

- **Preferred:** fold into 44-1's `AddTrackerCore` if undeployed (44-1 D4's scarcity argument, and 44-7 makes the same call for `AutomationMode` — **coordinate so all three land in one migration**).
- **Otherwise:** `AddTrackerExternalLink`, with an operator sweep (44-1).

The path taken is recorded in the PR.

## Events

Uses 44-5's constants; adds one:
- `WORKITEM.LINKED.SUCCESS` (44-5) — data `{ platform, repoFullName, number, source: "import" | "manual" }`
- `WORKITEM.UNLINKED.SUCCESS` — **new constant**, added to `WorkItemEvents.cs`, best-effort per 44-5 D6
- `WORKITEM.IMPORTED.SUCCESS` — **new**, one per import call (not per item), data `{ repoFullName, imported, skippedAlreadyLinked, skippedLinkedElsewhere, failed }`
- `WORKITEM.OUTBOUND.FAILED` — **new**, best-effort, `Data.statusCode` only, **no response body** (D7)

Each new constant is added to 44-5's `Transactional` / best-effort classification (its D6 test fails otherwise) and must not be allowlisted in the naming ratchet.

## Test Plan

| # | Test | Asserts |
|---|---|---|
| 1 | `ImportTests.Creates_work_items_with_external_refs` | ref fields, `Status = backlog` |
| 2 | `ImportTests.Rerun_skips_already_linked` | `skipped-already-linked`, **no update to the existing item** — **AC3 / D3** |
| 3 | `ImportTests.Rejects_non_github_with_a_named_code` | Gitea installation → `PLATFORM_IMPORT_UNSUPPORTED` — **AC5 / D2** |
| 4 | `ImportTests.Five_hundred_issues_mint_contiguous_keys_in_bounded_statements` | **AC4 / D4** |
| 5 | `IssueLabelMapperTests.Infers_kind_and_priority_from_labels` | incl. `critical`/`medium` alias folding — **D5** |
| 6 | `IssueLabelMapperTests.Unmatched_labels_are_preserved` | nothing dropped — **D5** |
| 7 | `LinkTests.Linking_an_already_linked_issue_is_409` | **AC6 / D9**, incl. a concurrent-link race |
| 8 | `OutboundTests.Only_comment_and_label_methods_are_called` | mock asserts no other platform method invoked — **AC7 / D6** |
| 9 | `OutboundTests.Mode_off_calls_nothing` | default-off proven |
| 10 | `OutboundTests.Failure_does_not_fail_the_status_change` | 502 injected → status persisted, `WORKITEM.OUTBOUND.FAILED` emitted — **AC8 / D7** |
| 11 | `OutboundTests.Failure_event_carries_no_response_body` | status code only — the secret-free rule |
| 12 | `DivergenceTests.LastKnownExternalState_is_never_refreshed` | mutate the fake platform; re-read; field unchanged — **AC9 / D8** |
| 13 | `NoSyncTests.Story_registers_no_hosted_service_or_webhook_handler` | DI reflection — **AC9 / D10, the mechanical guard** |
| 14 | `TrackerCatalogDescriptorTests.New_routes_have_descriptors` | extends 44-2 test 20 |
| 15 | `EventTypeNamingRatchetTests` (44-5) | the four new constants match the shape and are **not** allowlisted |

Tests 1–4, 7, 10, 12 are Testcontainers; the platform is a faked `IGitHubEngineCallbackService` throughout — **no live GitHub call in any test**.

## Definition of Done

- 15 tests green.
- `IGitPlatformClient` is **unmodified** — grep-checked. This story adds no method to the abstraction (D2), which is the mechanical statement of the epic's D3.
- No `IWebhookHandler` and no `IHostedService` added (test 13).
- `OutboundMode` defaults to `off` in the migration, the DTO and the UI.
- Outbound failure events carry no response body (test 11).
- The migration path taken (folded into `AddTrackerCore` with 44-7's column, or standalone) is recorded, and coordinated with 44-7.
- A `.dev/findings/` note recording the platform-abstraction gap inventory from the Architectural Context — twelve methods, no issue CRUD, a dead `Issue` record, two missing drivers, zero webhook handlers — so the next person proposing sync starts from evidence.

## Dependencies & Sequencing

- **Blocked by:** 44-0, 44-1, 44-2, 44-5.
- **Blocks:** nothing. Can land last, or be cut, without affecting the rest of the epic — which is why it is P2.
- **Coordination:** 44-7 also adds a `projects` column. **Land the two column additions in one migration** if `AddTrackerCore` is still undeployed.
- **Shared-edit register:** `TrackerEndpoints.cs`, `Program.cs` tracker group, `TrackerActionDescriptors.cs`, `WorkItemRepository.cs`, `TrackerService.cs`, `WorkItemEvents.cs` — all Epic 44 files, all additive.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| **Scope creep into sync.** The single largest risk in this story: every reviewer will ask "why not just refresh it?" | D8 and D10 argue it; test 12 pins non-refresh; test 13 asserts no hosted service or webhook handler; the DoD requires `IGitPlatformClient` to be unmodified. The findings note gives the next proposer the evidence rather than the instinct. |
| **A one-way import is mistaken for a sync by users.** | The wire says `snapshot`; `lastKnownExternalState` carries `capturedAt`; 44-6 renders the capture time beside the link. |
| **Import of a large backlog times out.** | Paginated source reads, `CreateManyAsync` block allocation, and a per-call cap with a continuation token. Test 4 is at 500; beyond that the caller re-invokes. |
| **Outbound comments spam a repository** when a project is opted in with many items already terminal. | Outbound fires only on a *transition* to terminal, never on the current state, so opting in does not retro-post. Stated in the DTO doc and covered by test 9's sibling. |
| **A secret leaks into an event.** | D7's status-code-only rule, copied from `ApplyTriageResultActivity.cs:257-263`, plus test 11. |
| **`ExternalRefJson` expression index is fragile** to a change in the JSON shape. | `ExternalRef`'s normalization (step 1) is the single writer of those two keys, and the unique index is created over the same expression the lookup uses. A shape change is a migration, and D7 of 44-1 chose `jsonb` precisely so that change is possible. |

## Effort Breakdown

| Task | Days |
|---|---|
| Steps 1, 3 (`ExternalRef`, label mapper) | 0.5 |
| Step 2 (import service: pagination, dedupe, bulk create, platform rejection) | 1.25 |
| Steps 4, 8 (outbound service + the `TrackerService` hook) | 0.75 |
| Steps 5–7, 9 (repository methods, endpoints, mapping, DI, descriptors) | 0.5 |
| Step 10 (15 tests) | 0.75 |
| Findings note, migration coordination with 44-7, review | 0.25 |
| **Total** | **4.0** |
