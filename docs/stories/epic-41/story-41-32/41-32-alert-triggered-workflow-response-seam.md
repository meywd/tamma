# Story 41-32: The Alert-Triggered Workflow-Response Seam — the reactive half of Epic 41's missing triggers

Status: drafted

## User Story

As a **tenant**, I want a raised alert to be able to **start a workflow**, not only send a
notification, so that the incident-response and security-incident workflows this epic specifies as
*"reactive trigger"* have something that can actually trigger them — once per alert, tenant-scoped,
and governed by the same autonomy dial as everything else.

## Priority

**P1 / Wave 0.** It is the **reactive sibling of 41-30**. Between them they own the two trigger classes
Epic 41 assumes and no story provides: *scheduled* (41-30) and *reactive* (this).

## The gap, verified

Two Epic 41 stories declare a reactive trigger in their own Scope line:

- **41-21**: *"Reactive trigger (security alert / event) → thin binding over `document-lifecycle`"*
- **41-22**: *"Reactive trigger (alert / health-review escalation) → thin binding(s) over
  `document-lifecycle`"*

**Nothing in the platform can deliver that trigger.** The detection stack is complete and the response
stack is drafted, and there is no edge between them:

- `AlertRuleEvaluator` (`Tamma.Api/Services/Alerts/Rules/AlertRuleEvaluator.cs`) polls the DCB stream,
  matches `alert_rules` rows by `EventType` + `Predicate`, and raises through `IAlertSink`.
- `NotificationDispatcher` then delivers to `Channels/` — `EmailAlertChannel`, `PagerDutyAlertChannel`,
  `SlackAlertChannel`, `WebhookAlertChannel`. **All four are notification sinks.**
- `IElsaWorkflowService` — the only way anything in `Tamma.Api` starts a workflow — has exactly five
  consumers: `MentorshipController`, `DocumentDecisionSubmissionService`, `AdlEndpoints`,
  `RotationTriggerService`, and its DI registration. **The alerts subsystem is not among them.**
- The six built-in rules (`BuiltInAlertRules.cs:32-108` — `budget-exhausted`,
  `agent-dispatch-failed-3x-5min`, `workflow-retry-exceeded`, `platform-api-unhealthy`,
  `secret-rotation-failed`, `audit-chain-tamper`) each end their description with a human instruction
  ("Check…", "manual reconciliation required", "Treat as a potential integrity incident"). That is not
  an oversight — it is an accurate description of the only thing the system can do.
- The alert HTTP surface (`Program.cs:2177-2209`) is read / acknowledge / resolve. Nothing starts
  anything.

So Tamma detects, pages a human, and stops. Every Epic 41 workflow that is supposed to *respond* is
unreachable except by a person who read the email and manually invoked something — and for 41-21 and
41-22 there is no manual invocation surface either.

## Amendment, not a new workflow — and the narrow reason

**This story adds no workflow.** 41-21 and 41-22 are the workflows; they are drafted and their shape is
settled. What is missing is the **edge**, and the right place for it is inside the alerting stack that
already does the hard parts:

- rule matching against the DCB stream, with predicates and `group_by` correlation,
- per-rule enable/disable, severity override and throttle,
- tenant scoping (`Alert.TenantId` nullable = platform-scoped),
- admin CRUD and a seeded built-in set with an edit-protection policy.

Rebuilding any of that beside it would be a second alert engine. So: **one new binding table
(`alert_responses`), one `IAlertResponder` beside the existing channel dispatch, one claim ledger, and
the Epic 43 governance hookup.** The alert engine is amended; nothing about the workflow layer changes.

> **Why not "just add a fifth channel type."** Tempting, and rejected. A channel is a *notification*
> target: `NotificationDispatcher` retries it with backoff through `AlertDeliveryAttempt`, which is
> **at-least-once** by design and correct for an email. At-least-once dispatch of an incident workflow
> means duplicate incident workflows on a flaky tick. The response path needs **at-most-once**, so it
> gets its own claim ledger (`UNIQUE (alert_id, response_id)`) rather than riding the delivery-attempt
> retry machinery. Everything upstream of the dispatch is shared; only the delivery semantics fork.

## Scope

1. **`alert_responses`** (control plane) — the binding. `alert_rule_id` (fk), `tenant_id` (nullable —
   null = platform template, materialised per tenant exactly as 41-30 D6 does), `definition_id` (the
   target workflow, **from a closed schedulable/dispatchable allowlist**), `input_mapping_json` (which
   alert/event fields become which workflow inputs), `min_severity`, `enabled`, audit columns.
2. **`alert_response_dispatches`** — the at-most-once ledger. `UNIQUE (alert_id, response_id)`;
   claimed before dispatch, outcome stamped after.
3. **`IAlertResponder` + `WorkflowAlertResponder`** — invoked on the same path that raises an alert,
   after the notification fan-out, never before it. **A responder failure must never suppress a
   notification** — the human page is the fallback and it is the more important of the two.
4. **Epic 43 governance.** Dispatching a workflow in response to an alert is a governed action:
   `effect:alert.response.dispatch`, and the *target* workflow's own actions stay governed as they
   already are. Below the configured autonomy level the response is **not** silently skipped — it
   raises the alert with a `response_pending_authorization` note so a human sees that automation was
   available and gated.
5. **Admin API + per-mode RBAC**, answered separately (see AC5).

## Explicitly out of scope

- **Any new workflow.** 41-21 and 41-22 are the consumers; this story dispatches them and edits
  neither.
- **Migrating the two existing reactive bridges.** `TenantCleanupRequestedTrigger` and
  `TenantDeleteRequestedTrigger` are `platform_events` → **Elsa `Event`-starter** bridges (they publish
  an Elsa event that a workflow's `Event` activity is waiting on), which is a different mechanism from
  dispatching a definition. They are also evidence for this story's thesis — two hardcoded copies of
  "react to an event type" already exist — but converting them is behaviour change to landed,
  working tenant-lifecycle code for no consumer benefit. Recorded, not done.
- **Auto-resolution / auto-remediation policy.** This story starts a workflow; whether that workflow
  may *act* is Epic 43's dial and Epic 42's tools, unchanged.
- **New alert rules.** The six built-ins are untouched; this story lets an admin bind a response to
  any of them.

## Events

`ALERT.RESPONSE.DISPATCHED` (tags `tenantId`, `alertId`, `ruleId`, `definitionId`, `instanceId`),
`ALERT.RESPONSE.SUPPRESSED` (already dispatched for this alert — INFO),
`ALERT.RESPONSE.GATED` (autonomy/authorization refused it — LOUD, because a gated response is a thing
a human must know happened),
`ALERT.RESPONSE.FAILED` (LOUD — dispatch threw; the notification still went out).

## Autonomy behavior

- **70–84:** a bound response is **proposed**, not run: the alert carries
  `response_pending_authorization` and a holder of the owning role authorizes it. `ALERT.RESPONSE.GATED`
  is emitted.
- **85–100:** the orchestrator may authorize automatically, per the rules for
  `effect:alert.response.dispatch` and for the target workflow's own actions.
- A response whose target workflow would take an always-escalate action is gated regardless of the
  dial — the target's own gates are not bypassed by having been started automatically. **Starting a
  workflow is never a way around that workflow's governance**, and a test pins it.

## Acceptance Criteria

1. **At most one dispatch per `(alert, response)`, durably** — proven across two concurrent evaluator
   ticks and across a process kill between claim and dispatch.
2. **A responder failure never suppresses a notification.** Ordering and isolation are pinned by a
   test in which the responder throws and the email/Slack channel still delivers.
3. **Target-agnostic**, from a **closed allowlist** of dispatchable definition ids validated at write
   time. A test asserts `delete-tenant` is rejected by the admin API — an alert rule that can start any
   workflow is a privilege-escalation primitive.
4. **Tenant scoping.** A tenant-scoped alert dispatches only that tenant's response bindings, with the
   `tenantId` threaded into the workflow inputs; a platform-scoped alert (`TenantId == null`) dispatches
   only platform bindings. Neither can reach the other.
5. **Per-mode RBAC.** single-user: the sole user may bind responses. SaaS: `tenant_owner` /
   `tenant_admin` for their own tenant's bindings; **platform-owner only** for `tenant_id IS NULL`
   template rows (a tenant must not author a binding that materialises into every other tenant);
   `member` ⇒ 403 on write, 200 on read.
6. **Autonomy gating is visible, not silent** — a gated response emits `ALERT.RESPONSE.GATED` and
   annotates the alert; it never looks like nothing was bound.
7. **The target workflow's own governance is unchanged** — a workflow started by an alert hits exactly
   the same accept gates, escalation classes and catalog checks as one started by a human.
8. **Disabled by default:** the feature ships with zero seeded bindings, so landing it changes no
   deployment's behaviour until an admin binds something.

## Dependencies

- **Blocking:** nothing. `AlertRuleEvaluator`, `IAlertSink`, `NotificationDispatcher`, `alert_rules`,
  `alerts` and `IElsaWorkflowService` are all landed.
- **Blocks / unblocks:** **41-21** and **41-22** — their Scope's "reactive trigger" becomes real. Both
  should be revised to name this seam in their Dependencies (they currently name no trigger mechanism
  at all, because none existed to name).
- **Interlocks with 41-30** — the two trigger seams. They share the *claim-ledger* idiom and the
  *closed target allowlist* decision, deliberately; they share no table (the scheduled key is
  `(trigger, window)`, the reactive key is `(alert, response)`). **Land 41-30 first if you want the
  idiom reviewed once**; neither blocks the other.
- **Interlocks with Epic 43** — `effect:alert.response.dispatch` is a new catalog member in
  `platform-automation`; the enforcement point is the responder. Coordinate the group count bump with
  41-30's.
- **Related:** **41-31** — an alert that can reach `emergency-rollback` is materially more useful than
  one that can reach a document. That combination is also the sharpest reason AC7 exists.
- **Not related:** Epic 42. This seam needs no tool; the *workflows it starts* may.

## Estimated Effort

**4–5 days.**
