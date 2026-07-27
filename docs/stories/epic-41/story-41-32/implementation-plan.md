# Implementation Plan — Story 41-32: The Alert-Triggered Workflow-Response Seam

> **Read 41-30's plan first if you are building both.** They share two decisions on purpose — the
> claim-ledger idiom (`INSERT … ON CONFLICT DO NOTHING` as the dedupe answer) and the closed
> dispatchable-definition allowlist. They share **no table and no code**. If 41-30 has landed, review
> its `TryClaimFireAsync` and mirror its shape here rather than inventing a second style.

## Scope & Deliverable

Two control-plane tables, one `IAlertResponder` seam wired into the existing alert-raise path, one
admin API, one `ALERT.RESPONSE.*` event family, one Epic 43 catalog member. **Zero new workflows,
zero taxonomy changes, zero document types.**

## Pre-Reading

- `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs` — the DCB poll →
  rule-match → `IAlertSink.RaiseAsync` path; note the `group_by` correlation and the per-rule throttle
- `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/NotificationDispatcher.cs` and `Channels/`
  (`EmailAlertChannel`, `PagerDutyAlertChannel`, `SlackAlertChannel`, `WebhookAlertChannel`) — **read
  the retry/backoff path through `AlertDeliveryAttempt` before deciding where the responder hooks**;
  it is at-least-once, and that is the whole reason the responder is not a channel
- `apps/tamma-elsa/src/Tamma.Data/Entities/AlertRule.cs` — `EventType`, `Predicate`, `IsBuiltIn` /
  `BuiltInKey` and the built-in edit-protection policy (an admin may disable and re-link but not
  re-predicate); `Alert.cs` — the `active|acknowledged|resolved` lifecycle and the **nullable
  `TenantId`** that AC4's isolation rule turns on
- `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Rules/BuiltInAlertRules.cs:32-108` — the six rules a
  response can bind to, and the human-instruction descriptions that document the gap
- `apps/tamma-elsa/src/Tamma.Api/Services/ElsaWorkflowService.cs:132-151` (`StartWorkflowAsync`) and
  its five call sites — **the seam this story becomes the sixth consumer of.** Note
  `MentorshipController.cs:79` passes `"tamma-autonomous-mentorship"`, a definition id that **does not
  exist** — a live example of what an unvalidated definition-id string costs, and the direct argument
  for D4's allowlist
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:2177-2209` — the alert HTTP surface the admin API extends
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TenantCleanupRequestedTrigger.cs` and
  `TenantDeleteRequestedTrigger.cs` — the **two existing hardcoded reactive bridges**. Read them for
  the evidence, not for the pattern: they publish an Elsa `Event` that a workflow's `Event` **starter**
  is waiting on, which is a different mechanism from dispatching a definition (D7)
- `docs/stories/epic-41/story-41-21/41-21-security-incident-analysis.md:18` and
  `story-41-22/41-22-incident-response-and-postmortem.md:18` — the two "Reactive trigger" Scope lines
  this story exists to satisfy
- `docs/stories/epic-43/story-43-3/43-3-groups-and-behaviour-preserving-defaults.md:129-130` — where
  `effect:alert.response.dispatch` lands
- **NOT FOUND (verified):** any reference to `IElsaWorkflowService`, `Workflow`, `Dispatch`,
  `Remediat` or `Mitigat` anywhere in `AlertRuleEvaluator.cs` or `NotificationDispatcher.cs`; any
  incident/mitigation/kill-switch workflow definition; any `alert_responses`-shaped table

## Corrections to the story

1. **CONFIRMED — the alert stack and the workflow layer share no edge.** `IElsaWorkflowService` has
   five consumers and none is in `Services/Alerts/`. The story's central claim holds exactly as
   written.

2. **NEW — the responder must run on the raise path, not on the delivery path, and the two must not
   share a failure domain.** `NotificationDispatcher` owns retry/backoff per channel through
   `AlertDeliveryAttempt`. Hooking the responder inside it would (a) inherit at-least-once and (b) put
   a workflow dispatch inside a retry loop whose failures already have their own semantics.
   **Decision (D2): the responder is invoked by the alert-raise path, after the notification fan-out is
   *enqueued* (not after it is delivered), in its own try/catch.** AC2's ordering test pins it.

3. **NEW — the natural idempotency key is the alert id, and that is better than a time window.** Unlike
   41-30, this seam needs no window arithmetic and no cron: one alert row is one event, and a binding
   should fire for it at most once, forever. `UNIQUE (alert_id, response_id)` is total and needs no
   clock. Do **not** copy 41-30's `windowKey` here; it would be a worse key for the same job.

4. **NEW — `min_severity` is required on the binding, not optional.** `AlertRule.Severity` is
   admin-overridable and `Alert.Severity` is `critical|warning|info`. Without a floor on the binding, a
   rule re-severitised from `critical` to `info` by an admin quietly keeps starting incident workflows.
   Default `critical`.

5. **NEW — the alert may be re-raised; the response must not re-fire, and "resolve then re-raise" is a
   real sequence.** The alert engine can raise a *new* alert row for a recurring condition (throttled
   per rule). A new alert row is a new `alert_id`, so the ledger will permit a new dispatch — which is
   correct (a second outage deserves a second response) but is a foot-gun if the rule's throttle is
   loose. **Mitigation (D5): the binding carries `cooldown_seconds` (default 3600) checked against the
   most recent `alert_response_dispatches` row for the same `(response_id, tenant_id)`** — a second
   guard *above* the per-alert ledger, not instead of it.

6. **NEW — AC7 needs a mechanism, not just an assertion.** "Starting a workflow via an alert does not
   bypass that workflow's governance" is true **by construction** — the responder calls
   `IElsaWorkflowService.StartWorkflowAsync`, the same entry every other caller uses, and every accept
   gate / escalation class / catalog check lives inside the workflow. The plan's job is to make that
   *checkable*: the test asserts the responder reaches `StartWorkflowAsync` and nothing else — in
   particular it never sets an autonomy override, never passes an `acceptanceRulesJson` of its own, and
   never writes a document. D6.

## Design Decisions

- **D1 — `alert_responses` (control plane).** `id`, `alert_rule_id` (fk → `alert_rules`), `tenant_id`
  (uuid **nullable**; null = platform template, materialised per tenant on first match — the 41-30 D6
  decision, same reasoning), `definition_id` (text, allowlist-validated at write time),
  `input_mapping_json` (jsonb; a flat map of workflow-input-name → a `$.alert.*` / `$.event.*` path),
  `min_severity` (text, default `critical`, D4), `cooldown_seconds` (int, default 3600, D5),
  `enabled` (bool default true), audit columns.
  `UNIQUE NULLS NOT DISTINCT (alert_rule_id, tenant_id, definition_id)`.
  Residency: control plane, with `alert_rules`/`alerts` — they are already there and the responder must
  read across tenants. **Excluded from the destructive startup DROP list** (`Tamma.Api/Program.cs:3243-3282`),
  same call and same reason as 41-30 D9 and 43-5: a deploy must not silently unbind every response.

- **D2 — `alert_response_dispatches` + the invocation point** (Corrections 2, 3).
  Columns: `id`, `alert_id`, `response_id`, `tenant_id`, `definition_id`, `claimed_at`,
  `dispatched_at?`, `workflow_instance_id?`, `outcome` (`claimed|dispatched|gated|failed`), `detail?`.
  **`UNIQUE (alert_id, response_id)`**, claimed with `INSERT … ON CONFLICT DO NOTHING` before the
  dispatch; 0 rows affected ⇒ `ALERT.RESPONSE.SUPPRESSED` and return.
  Invocation: a new `IAlertResponder.RespondAsync(Alert, AlertRule, matchedEvent, ct)` called from the
  alert-raise path immediately **after** the notification fan-out is enqueued, inside its own
  `try/catch` that logs and emits `ALERT.RESPONSE.FAILED` and **returns normally**. AC2.

- **D3 — `WorkflowAlertResponder` is the only implementation, and it is thin.** Load enabled bindings
  for the rule (tenant-matched per AC4, templates materialised), filter by `min_severity` (D4) and
  `cooldown_seconds` (D5), gate (D6), claim (D2), build inputs from `input_mapping_json` **fail-closed**
  (an unresolvable path ⇒ the input is omitted and the omission is recorded in `detail`, never
  substituted with a default), call `IElsaWorkflowService.StartWorkflowAsync(definitionId, inputs)`,
  stamp the outcome, emit. **One binding's failure never affects another's** — the loop isolates per
  binding, the `QueuedTaskRepository.ListPendingFromAnyTenantAsync` discipline.

- **D4 — a closed dispatchable-definition allowlist, validated at write time** (AC3). The
  `MentorshipController.cs:79` dangling definition id (`"tamma-autonomous-mentorship"`, which exists
  nowhere) is the standing proof that unvalidated definition-id strings rot silently. The allowlist is
  a code constant, not config, and it is **the same list 41-30 introduces** — if 41-30 has landed,
  reference it; if not, create it here in `Tamma.Core` and note the shared ownership so the second
  story consumes rather than copies. Initial members: the reactive consumers
  (`security-incident-analysis`, `incident-response`) plus `emergency-rollback` (41-31) and
  `rotate-secret`. **Explicitly not on it:** `delete-tenant`, `clean-up-failed-tenant`,
  `create-tenant`, `deployment-pipeline`, `single-issue-cycle`.

- **D5 — two guards, not one** (Correction 5). Per-alert (the ledger, absolute) and per-binding
  cooldown (time-based, tunable). A recurring condition that the rule's own throttle lets through still
  cannot start an incident workflow every minute.

- **D6 — governance: gate visibly, never silently, and never weaken the target** (Correction 6, AC6,
  AC7). Before claiming, evaluate `effect:alert.response.dispatch` against the tenant's autonomy dial
  and catalog assignment (Epic 43's resolver). Refused ⇒ write a `gated` ledger row, emit
  `ALERT.RESPONSE.GATED` (LOUD), annotate the alert with `response_pending_authorization`, and **return
  without dispatching**. Never a silent skip: an operator must be able to see that automation was
  available and was held.
  The responder passes **no** autonomy override, **no** `acceptanceRulesJson`, and writes **no**
  document — the target workflow resolves its own governance exactly as it does for a human caller.
  A test asserts the `StartWorkflowAsync` input dictionary contains only the mapped inputs plus
  `tenantId` / `alertId` / `correlationId`.
  **If Epic 43 has not landed**, the gate degrades to "platform-configured on/off per binding" and the
  story says so in its ACs rather than shipping an ungated dispatcher.

- **D7 — the two existing bridges are evidence, not a migration target.**
  `TenantCleanupRequestedTrigger` / `TenantDeleteRequestedTrigger` poll `platform_events` and
  `IEventPublisher.PublishAsync` an Elsa event that a workflow's `Event` **starter activity** is
  waiting on. That is Elsa's trigger-index mechanism; this seam calls
  `StartWorkflowAsync(definitionId, …)`. Unifying them would mean either giving 41-21/41-22 `Event`
  starters (coupling two document producers to a bespoke event name each) or rewriting two working,
  cooling-off-aware tenant-lifecycle bridges. **Do neither.** Record the duplication in the code
  comment so the third person to want a reactive trigger finds this seam first.

- **D8 — rejected alternative: a fifth `IAlertChannel`.** Stated in the story; the reason is delivery
  semantics (`AlertDeliveryAttempt` is at-least-once with backoff, correct for email, wrong for
  starting an incident workflow). Recorded here too because it is the change a reviewer will propose.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm: `AlertRuleEvaluator` →
   `IAlertSink` → `NotificationDispatcher` path; `AlertRule`/`Alert` entities; `IElsaWorkflowService`
   registration (`Program.cs:355`); the DROP list. Confirm 41-30's status — if landed, reuse its
   allowlist constant and mirror its claim idiom (D4).

2. **CREATE** `apps/tamma-elsa/src/Tamma.Data/Entities/AlertResponse.cs` +
   `AlertResponseDispatch.cs`; **MODIFY** `ControlPlaneDbContext.cs` + `TammaModelConfiguration.cs`
   (two `DbSet`s, the two unique indexes, `min_severity` / `outcome` CHECKs).

3. **CREATE the control-plane migration** (`AddAlertResponses`).
   **MODIFY** `Tamma.Api/Program.cs` — **do not** add either table to the DROP list; extend the
   standing exclusion comment (shared with 41-30 — write it once).

4. **CREATE** `apps/tamma-elsa/src/Tamma.Api/Services/Alerts/Responses/IAlertResponder.cs`,
   `WorkflowAlertResponder.cs`, `AlertResponseInputMapper.cs` (pure, total, fail-closed — the
   `$.alert.*` / `$.event.*` resolver, with its own unit test), and
   `AlertResponseEvents.cs` (the four constants; `GATED` and `FAILED` error-status).

5. **CREATE** `apps/tamma-elsa/src/Tamma.Data/Repositories/AlertResponseRepository.cs` — binding load
   (tenant-matched + template materialisation), `TryClaimDispatchAsync` (**the correctness core**),
   `StampOutcomeAsync`, `LastDispatchAtAsync` (D5's cooldown read).

6. **MODIFY the alert-raise path** to invoke `IAlertResponder` after the notification fan-out is
   enqueued, in its own try/catch (D2). Register a `NullAlertResponder` as the default so a composition
   root without the feature is unaffected; register `WorkflowAlertResponder` where
   `IElsaWorkflowService` is available.

7. **CREATE** `apps/tamma-elsa/src/Tamma.Api/Endpoints/Alerts/AlertResponseEndpoints.cs` —
   `GET/POST/PUT/DELETE /api/alerts/rules/{ruleId}/responses`, with D4's allowlist validation and AC5's
   per-mode RBAC. **MODIFY** `Program.cs` to map them beside the existing alert routes (`:2177-2209`).

8. **Epic 43 registration** — one `ExternalEffect` member `effect:alert.response.dispatch` in
   `platform-automation`; extend 43-3's expected-set and totals. **Coordinate the bump with 41-30's
   four members** so `platform-automation` moves once. If Epic 43 has not landed, apply D6's degraded
   gate and say so in the ACs.

9. **CREATE the tests**; full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean.

## Data & Migrations

Two control-plane tables in one migration (D1, D2), both excluded from the destructive startup DROP
list. No tenant-schema migration. No event-schema change — `ALERT.RESPONSE.*` rides the existing drain.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the claim race).

- **`AlertResponseRepositoryTests`** (Testcontainers, **AC1**) — two concurrent
  `TryClaimDispatchAsync` for the same `(alert, response)` ⇒ exactly one true; a third after commit ⇒
  false; a **different** alert id for the same response ⇒ true (a second outage may respond);
  template materialisation idempotent and per active tenant only.
- **`AlertResponseInputMapperTests`** — resolves `$.alert.severity`, `$.event.data.repository`;
  an unresolvable path ⇒ the input is **omitted** and named in `detail` (never defaulted, never
  crashed); a malformed mapping ⇒ empty map + a recorded reason.
- **`WorkflowAlertResponderTests`** (Moq) —
  (a) a bound, enabled, above-`min_severity` response ⇒ exactly one `StartWorkflowAsync`, with the
  mapped inputs plus `tenantId`/`alertId`/`correlationId` **and nothing else** (**AC7**, D6);
  (b) below `min_severity` ⇒ no dispatch (**D4**);
  (c) within `cooldown_seconds` ⇒ no dispatch, `SUPPRESSED` (**D5**);
  (d) gate refuses ⇒ `gated` ledger row + `ALERT.RESPONSE.GATED` + alert annotation + **no**
  `StartWorkflowAsync` (**AC6**);
  (e) `StartWorkflowAsync` throws ⇒ `ALERT.RESPONSE.FAILED`, method returns normally;
  (f) three bindings, the second throws ⇒ the first and third still dispatch;
  (g) a tenant-scoped alert never loads a platform binding and vice versa (**AC4**).
- **`AlertResponderDoesNotSuppressNotificationTests`** (**AC2**) — the raise path with a responder that
  throws; assert the channel fan-out is still enqueued, and assert the **ordering** (fan-out enqueued
  before `RespondAsync` is entered).
- **`AlertResponseEndpointsTests`** (`WebApplicationFactory`, **AC3, AC5**) — `definition_id:
  "delete-tenant"` ⇒ 400 and **no row written**; a `member` POST ⇒ 403; a `tenant_admin` writing a
  `tenant_id: null` template ⇒ 403; a `tenant_admin` writing another tenant's binding ⇒ 403/404.
- **`NoSeededBindingsTests`** (**AC8**) — a fresh migration + the built-in rule seeder produces
  **zero** `alert_responses` rows.
- **`AlertResponseTablesNotDroppedOnStartupTests`** — the DROP-list text pin (shared idiom with 41-30).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — at-most-once per `(alert, response)` | 2, 3, 5 (D2) | repository claim race + post-commit re-claim |
| 2 — a responder failure never suppresses a notification | 6 (D2) | `AlertResponderDoesNotSuppressNotificationTests` |
| 3 — closed allowlist, validated at write time | 4, 7 (D4) | endpoint `delete-tenant` 400 |
| 4 — tenant / platform binding isolation | 5 (D1, D3) | responder test (g) |
| 5 — per-mode RBAC | 7 | endpoint tests |
| 6 — gating is visible, never silent | 4, 6 (D6) | responder test (d) |
| 7 — the target workflow's governance is unchanged | 4 (D6) | responder test (a) — inputs contain nothing governance-shaped |
| 8 — no seeded bindings | 2, 3 | `NoSeededBindingsTests` |

## Risks & Mitigations

- **An alert that starts a workflow is an automation amplifier.** A noisy rule bound to an expensive
  workflow burns budget and fills the decision inbox. Mitigations: `min_severity` (D4),
  `cooldown_seconds` (D5), the per-alert ledger (D2), the Epic 43 gate defaulting to *held* below the
  configured level (D6), and zero seeded bindings (AC8). Four independent brakes, deliberately.
- **The allowlist is the security boundary and it is a one-line mistake to widen.** Mitigation: it is a
  code constant with a negative test naming `delete-tenant`; D4 records why config would be wrong.
- **Reviewer pressure to "just make it a channel."** Mitigation: D8 states the delivery-semantics
  reason, and AC1's at-most-once test would fail under `AlertDeliveryAttempt`'s retry.
- **Epic 43 ordering.** If the catalog is not there, D6's degraded gate is the honest fallback — but
  **do not ship an ungated dispatcher and call it a follow-up**; the degraded mode is a per-binding
  on/off owned by the platform, which is weaker but not absent.
- **Duplication with 41-30 if built independently.** Mitigation: D4 says share the allowlist constant,
  step 3 says write the DROP-list exclusion comment once, step 8 says bump `platform-automation` once.
  Assign both stories to the same pair if possible.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check | 0.25 |
| 2–3 | Two entities + model config + migration + DROP-list exclusion | 0.75 |
| 4 | Responder + input mapper + events | 1.0 |
| 5 | Repository incl. the claim | 0.75 |
| 6 | Raise-path wiring + null default | 0.5 |
| 7 | Admin API + allowlist + per-mode RBAC | 0.75 |
| 8 | Epic 43 registration | 0.25 |
| 9 | Tests (claim race, mapper, responder ×7, ordering, endpoints, two pins) | 1.25 |
| **Total** | | **5.5** (story estimate 4–5 days — **revise to 5–6**; the responder's seven behavioural cases and the ordering test are more than the story assumed) |

## Blocks / Blocked by

- **Blocked by:** nothing hard. Soft: Epic 43 for the real gate (D6 degrades honestly without it);
  41-30 for the shared allowlist constant and ledger idiom (either order works).
- **Blocks / unblocks:** **41-21**, **41-22** — both must be revised to name this seam in their
  Dependencies. **Do not edit them from this story** — file the revision.
- **Related:** **41-31** — the highest-value binding this seam enables (alert → `emergency-rollback`),
  and the reason AC7 is written as a hard pin rather than a note.
- **Shared-file register (coordinate before editing):** `ControlPlaneDbContext.cs` +
  `TammaModelConfiguration.cs` + `Migrations/ControlPlane/` (with 41-30 and every control-plane story —
  serialize the migrations); `Tamma.Api/Program.cs` DROP list + alert route block; 43-3's
  `platform-automation` expected set (with 41-30); the dispatchable-definition allowlist constant
  (with 41-30).
