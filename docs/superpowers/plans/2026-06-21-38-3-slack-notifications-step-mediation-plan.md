# Story 38-3 — Slack / Notifications Step Mediation (Class D) — Implementation Plan

> **Date:** 2026-06-21
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Re-point `Integration/SlackActivity` from the co-hosted, Slack-token-holding
`IIntegrationService` to an internal **`POST /api/v1/notifications/slack`** endpoint that writes a
post *intent* to a control-plane `slack_outbox` table, and an out-of-band **`OutboxSlackSender`** in
`Tamma.Api` that alone holds the Slack token, performs the post (`chat.postMessage`), and audits it.
This closes the last Class-D `VIOLATION-by-co-hosting` (design §1.2) and sets the **outbox pattern as
the template for fire-and-forget external effects**, which the forward-looking Class-E (Stripe/billing,
Epic 35) tie-in then inherits by design. The engine ends the story holding **no Slack token**.

**Story file:** `docs/stories/epic-38/story-38-3/38-3-slack-notifications-step-mediation.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1, §1.2 Class-D/E, §5.1, §5.2)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa`. Two processes: `Tamma.ElsaServer` (engine, no
secrets) + `Tamma.Api` (holds creds, audits). EF Core migrations (one snapshot — sequential, do NOT
branch). Tests via `sg docker -c "dotnet test ..."` (session docker group is stale; plain
`dotnet build` needs no wrapper). xUnit. **`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO synchronous Slack mediation.** A Slack post is fire-and-forget; it uses the outbox shape
  (`QueueWelcomeEmailActivity`), not the request/response shape (`/llm/call`). Do not add a "call
  Slack and return the result" path.
- **NO Stripe / billing code.** The Class-E tie-in is **documentation-only** in the story; this plan
  writes zero billing code. (38-4's analyzer will later guard a hypothetical `BillingActivity`.)
- **NO per-tenant Slack BYOK.** Today there is one platform `Slack:BotToken`. Per-tenant Slack keys via
  the Epic 29 cabinet are a future extension; the entity carries the XOR scoping room but the resolver
  uses the platform token.
- **NO change to the GitHub/JIRA/CI members of the `IIntegrationService` composite.** Only the Slack
  methods are deregistered from the engine here; GitHub is 38-1's job. Coordinate the split.
- **NO new tool loop / sanitizer.** Out of scope; this is a notification effect.

---

## Current-state findings (verified 2026-06-21, `feat/exec-wave-02`)

| Seam | Where it is today | How 38-3 uses it |
|---|---|---|
| **Slack activity** | `Tamma.Activities/Integration/SlackActivity.cs` — `CodeActivity<SlackOperationResult>`, injects `IIntegrationService`, branches on `Input<SlackAction>` (`SendChannel`/`SendDirect`/`SendAssessment`/`SendGuidance`/`SendNotification`), calls `SendSlackMessageAsync`/`SendSlackDirectMessageAsync` **in-engine**; formats via `FormatMessage`/`SendAssessmentRequest`/`SendGuidanceMessage`; logs a `MentorshipEvent` on success. | **Gut** to a thin client; move the formatting to the API; drop `IIntegrationService`; keep the `MentorshipEvent` local write. |
| **Composite integration service** | `Tamma.Core/Interfaces/IIntegrationService.cs` — composite with `SendSlackMessageAsync(channel,msg)` / `SendSlackDirectMessageAsync(userId,msg)` (token-holding impl in `Tamma.Api`, unregistered/null in engine). | **Deregister the Slack methods from the engine**; the API keeps its impl for `OutboxSlackSender`. |
| **Outbox template** | `Tamma.Activities/TenantLifecycle/QueueWelcomeEmailActivity.cs` → CP `platform_email_outbox` (`PlatformEmailOutboxMessage`) via `IPlatformEmailOutboxRepository.EnqueueWelcomeOnceAsync` (in-code pre-check + partial unique index `(TenantId, Template) WHERE Status <> 'failed'`); delivered out-of-band by `OutboxSmtpSender`; enqueue is non-fatal. | **Mirror verbatim** for `slack_outbox` / `SlackOutboxMessage` / `OutboxSlackSender` / exactly-once enqueue. |
| **Engine→API seam** | `Tamma.Activities/LlmCall/TammaApiClient.cs` — Bearer `Tamma:ApiToken` + `X-Tenant-Id`; `PostAsync<T>` / `PostVoidAsync` (null/false on failure) / `AddTenantHeader` / `RecordHealthAsync`. | **Add `QueueSlackNotificationAsync`** via `PostVoidAsync`. |
| **Engine-callback template** | `Tamma.Activities/Testing/TriggerCIActivity.cs` — POSTs to an internal `Engine:CallbackUrl/api/engine/trigger-ci`, holds no vendor key. | Reference for "step holds no external credential". |
| **CP DROP list + model test** | `Tamma.ElsaServer/Program.cs` "Wiping Tamma-managed public-schema tables"; `tests/.../Epic28/ControlPlaneDbContextModelTests.cs` `Model_Has_ExpectedControlPlaneEntities` (strict `BeEquivalentTo`). | **Append `slack_outbox` / `SlackOutboxMessage`** to both (AC8) — otherwise 2nd-boot fails. |

**Key insight:** the only genuinely new code is the *endpoint*, the *CP outbox entity/repo/migration*,
the *out-of-band sender* (token-holder), and the *thin-client gut* of one activity. Everything else is
copying the `platform_email_outbox`/`OutboxSmtpSender` pattern and the `TammaApiClient` callback pattern.

---

## Architecture

```
SlackActivity (Elsa, engine)                       -- thin client; NO token, NO IIntegrationService
        |  TammaApiClient.QueueSlackNotificationAsync(req)  (Bearer Tamma:ApiToken + X-Tenant-Id)
        v
POST /api/v1/notifications/slack  (Tamma.Api)      -- engine-only auth; writes INTENT only; 202
        |  ISlackNotificationService.EnqueueAsync   (formats server-side; de-dupes; inserts row)
        v
slack_outbox (CP table)  Status=pending            -- token NOT read here
        |
        v  (out-of-band)
OutboxSlackSender (Tamma.Api hosted)               -- THE token-holder (Slack:BotToken)
        |  chat.postMessage / DM-open  (the ONLY Slack HTTPS call)
        v
Slack  ->  row Status=sent + SentAt                -- NOTIFICATION.SLACK.SENT.SUCCESS (tenant IEventRepository)
           or backoff/failed                       -- NOTIFICATION.SLACK.SENT.FAILED (key-free)
```

Per-mode (CLAUDE.md two-scoping-model): single-user = `UserId`-scoped outbox row + user event store;
SaaS = `TenantId`-scoped row + tenant `t_<hex>` event store; data never cross-tenant. Token is the
platform `Slack:BotToken` in both modes (per-tenant Slack BYOK is a future extension). Mode from
`ITammaModeProvider`.

---

## Task breakdown

Order: T1 (records + CP entity) → T2 (repo + migration + DROP-list/model-test) → T3 (endpoint +
service + formatting) → T4 (`OutboxSlackSender` + audit) → T5 (thin `SlackActivity` + client method +
engine deregistration) → T6 (mode/credential-safety/dedup isolation). T1 is a prerequisite for all;
T3 needs T2; T5 needs T3.

### T1 — Records + CP outbox entity (`SlackNotificationRequest`, `SlackOutboxMessage`)

**Scope:** The wire request and the CP outbox row shape. No behaviour.

**Files (new):** `Tamma.Api/Services/Notifications/SlackNotificationRequest.cs`,
`Tamma.Data/Entities/SlackOutboxMessage.cs` (mirror `PlatformEmailOutboxMessage`: `Id`, `TenantId?`,
`UserId?` XOR, `Action`, `Channel?`, `UserHandle?`, `FormattedMessage`, `SessionId?`, `CorrelationId`,
`Status`, `Attempts`, `MaxAttempts`, `NextAttemptAt`, `LastError?`, `CreatedAt`, `UpdatedAt`, `SentAt?`).

**Tests (first):** `Tamma.Api.Tests/Notifications/SlackNotificationRequestTests.cs` — required fields
enforced (`Action`, `Message`, `CorrelationId`); `MessageType` defaults to `Info`; record equality.

**Acceptance:**
- [ ] `SlackNotificationRequest` carries the AC3 fields.
- [ ] `SlackOutboxMessage` mirrors `PlatformEmailOutboxMessage` shape with Slack-specific columns.
- [ ] Builds clean; no analyzer warnings.

### T2 — Repository + migration + CP registration (AC8/AC9)

**Scope:** EF mapping for `SlackOutboxMessage`, the exactly-once `EnqueueOnceAsync`, the partial unique
index, and the destructive-DROP-list + model-contract-test registration.

**Files:** new `Tamma.Data/Repositories/ISlackOutboxRepository.cs` (`EnqueueOnceAsync`,
`ClaimPendingAsync`, `MarkSentAsync`, `MarkFailedAsync`), `Tamma.Data/Repositories/SlackOutboxRepository.cs`;
new `Tamma.Data/Migrations/<ts>_AddSlackOutbox.cs` (amends the existing snapshot — sequential, do NOT
branch); modify `Tamma.ElsaServer/Program.cs` (append `slack_outbox` to the public-schema DROP list);
modify `tests/.../Epic28/ControlPlaneDbContextModelTests.cs` (add `SlackOutboxMessage` to the strict
`BeEquivalentTo` list); EF model config in `Tamma.Api/Program.cs` (or the CP DbContext config).

**Partial unique index:** `(CorrelationId, Action, COALESCE(Channel, UserHandle)) WHERE Status <> 'failed'`
(mirrors `(TenantId, Template) WHERE Status <> 'failed'`).

**Tests (first):** `Tamma.Api.Tests/Notifications/SlackOutboxRepositoryTests.cs` — `EnqueueOnceAsync`
inserts one row; a second enqueue with the same `(correlationId, action, target)` returns the existing
id, inserts nothing; `ClaimPendingAsync` returns only `pending` rows past `NextAttemptAt`;
`MarkSent`/`MarkFailed` transition state. **2nd-test-host-boot test** (no `relation already exists`) and
`ControlPlaneDbContextModelTests` green.

**Acceptance:**
- [ ] Exactly-once enqueue on replay (DB-enforced + in-code pre-check).
- [ ] `slack_outbox` is in the DROP list (2nd boot succeeds) and the CP model test passes.
- [ ] Docker-bound repo tests green via `sg docker -c "dotnet test ..."`.

### T3 — Endpoint + notification service (formatting + enqueue) (AC1/AC2/AC3)

**Scope:** `POST /api/v1/notifications/slack` (engine-only auth), `ISlackNotificationService.EnqueueAsync`
that formats the message server-side (move `FormatMessage`/assessment/guidance templating from
`SlackActivity`), de-dupes, inserts the row, emits `NOTIFICATION.SLACK.QUEUED.SUCCESS`, returns 202.
**Never reads the token; never calls Slack.**

**Files:** new `Tamma.Api/Endpoints/NotificationEndpoints.cs`,
`Tamma.Api/Services/Notifications/ISlackNotificationService.cs` +
`SlackNotificationService.cs`; modify `Tamma.Api/Program.cs` (map endpoint; register service).

**Tests (first):** `Tamma.Api.Tests/Notifications/NotificationEndpointsTests.cs` — 401 missing bearer;
202 + `{ outboxId }` + one `slack_outbox` row + **Slack-HTTP-never-called** (spy) + **token-not-read**
(the sender's token source is untouched); `tenantId` derived from `X-Tenant-Id`; server-side formatting
matches the legacy `SlackActivity.FormatMessage` output; one QUEUED event.

**Acceptance:**
- [ ] Endpoint writes intent, returns 202, never calls Slack synchronously.
- [ ] Formatting moved server-side; raw token never read in the request path.
- [ ] DI resolves the endpoint at host startup (smoke / `WebApplicationFactory`).

### T4 — `OutboxSlackSender` (token-holder, out-of-band, audit) (AC4/AC7)

**Scope:** The single token-holder. A hosted/background scanner mirroring `OutboxSmtpSender`: read
`Slack:BotToken`, `ClaimPendingAsync`, post (`chat.postMessage` / DM-open), `MarkSent` + audit
`NOTIFICATION.SLACK.SENT.SUCCESS`, or backoff/`MarkFailed` + `NOTIFICATION.SLACK.SENT.FAILED`
(key-free `failureReason`).

**Files:** new `Tamma.Api/Services/Notifications/OutboxSlackSender.cs`; modify `Tamma.Api/Program.cs`
(register hosted; inject `IHttpClientFactory`, `ISlackOutboxRepository`, tenant `IEventRepository`).

**Tests (first):** `Tamma.Api.Tests/Notifications/OutboxSlackSenderTests.cs` — happy path (token read,
faked HTTP post, `sent` + `SentAt`, one SUCCESS event); transient failure (`LastError` key-free,
`Attempts++`, `NextAttemptAt` backoff, stays `pending`); terminal failure (`>= MaxAttempts` → `failed`
+ one FAILED event); **credential-safety**: token never appears in row/`LastError`/log/event; event
message preview is redacted/length-bounded.

**Acceptance:**
- [ ] Sender is the only token-holder; performs the only Slack HTTPS call.
- [ ] Failure path backs off then terminates; events emitted from the tenant `IEventRepository`.
- [ ] Token never leaks (credential-safety test passes).

### T5 — Thin `SlackActivity` + client method + engine deregistration (AC5/AC6/AC10)

**Scope:** Gut `SlackActivity` to a thin client; add `TammaApiClient.QueueSlackNotificationAsync`;
remove the engine's Slack `IIntegrationService` registration (coordinate the composite split with 38-1).

**Files:** modify `Tamma.Activities/Integration/SlackActivity.cs` (drop `IIntegrationService`; map
props → request; `QueueSlackNotificationAsync`; `SlackOperationResult{ WaitingForResponse=false }`;
keep `MentorshipEvent` local log; fail-soft); modify `Tamma.Activities/LlmCall/TammaApiClient.cs` (add
`QueueSlackNotificationAsync` via `PostVoidAsync`); modify `Tamma.ElsaServer/Program.cs` (remove engine
Slack registration).

**Tests (first):** `Tamma.Activities.Tests/Integration/SlackActivityThinClientTests.cs` — prop→request
mapping for each `SlackAction`; `SlackOperationResult{ Success=queued, WaitingForResponse=false }`;
`MentorshipEvent` logged on success; **fail-soft** (API down → `Success=false`, no throw); constructor
injects **no `IIntegrationService`**. **AC6 grep test:** zero `chat.postMessage`/`slack.com`/Slack
`IIntegrationService` usage under `Tamma.Activities` outside `TammaApiClient`.

**Acceptance:**
- [ ] `SlackActivity` holds no token and no `IIntegrationService`; queues via the client method.
- [ ] Engine registers no Slack-credential service.
- [ ] Fail-soft preserved; output contract unchanged for the mentorship workflows.

### T6 — Mode separation, dedup & credential-safety isolation

**Scope:** Prove per-mode scoping (`UserId` vs `TenantId`), replay dedup end-to-end, and zero token
leakage across the whole path; prove events land in the right store and never cross-tenant.

**Files:** extend the notification/sender tests with a mode matrix + a two-tenant isolation assertion.

**Tests (first):**
- single-user → outbox row `UserId` set / `TenantId` null; events in the user store.
- SaaS → `TenantId` set / `UserId` null; events in the tenant `t_<hex>` store.
- two tenants → rows + events tagged with their own scope; no leakage.
- replay → two enqueues for the same `(correlationId, action, target)` yield one row (end-to-end).
- token never appears in any row/response/log/event across the full path.

**Acceptance:**
- [ ] Mode matrix passes; scoping correct in both modes.
- [ ] Cross-tenant isolation holds; replay dedup holds end-to-end.

---

## Story order & dependencies

External siblings to coordinate (not hard blockers, but they touch the same shared composite /
template): **38-1** (deregisters the GitHub members of `IIntegrationService`; split the engine
deregistration cleanly), **38-2** (same `TammaApiClient`/endpoint template), **38-4** (lands its
allowlist after `QueueSlackNotificationAsync` exists so `TammaApiClient` is allowed). Internal:
T1 → T2 → T3 → T4 → T5 → T6. Downstream consumers (36 analytics, 37 audit, 35 billing-via-Class-E
pattern) are NOT blockers.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Notifications"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~Integration"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~ControlPlaneDbContextModelTests"
# AC6 no-violation check: engine holds no Slack token / makes no Slack call
grep -rn "chat.postMessage\|slack.com\|SendSlackMessageAsync\|SendSlackDirectMessageAsync" apps/tamma-elsa/src/Tamma.Activities
```

## Risks

- **CP-table 2nd-boot break (T2, AC8):** any new public-schema table not in the `Program.cs` DROP list
  fails the 2nd test-host boot with `relation already exists`, and the strict `ControlPlaneDbContextModelTests`
  list rejects the new entity. Mitigation: append `slack_outbox`/`SlackOutboxMessage` to both in T2;
  the 2nd-boot test is the net.
- **Engine still holds a token (T5, AC6):** mitigation: remove the engine Slack registration; grep the
  activities for Slack calls → zero; 38-4 makes it permanent.
- **Replay double-post (T2/T6, AC9):** mitigation: partial unique index `WHERE Status <> 'failed'` +
  in-code pre-check (mirrors `EnqueueWelcomeOnceAsync`); end-to-end dedup test.
- **Token leak into a row/event/log (T4, AC7):** mitigation: token read only in `OutboxSlackSender`;
  `LastError` key-free; event message preview redacted/length-bounded; credential-safety test asserts
  zero occurrences.
- **Dangling shared-composite registration with 38-1:** mitigation: coordinate the `IIntegrationService`
  deregistration split (Slack here, GitHub in 38-1) before either lands.
- **EF parallel-migration hazard:** mitigation: this story amends the existing snapshot (one
  `AddSlackOutbox` migration), implemented sequentially relative to 38-1/38-2/38-4 — never branch the
  snapshot.
- **Output-contract drift breaks mentorship workflows (T5, AC5):** mitigation: preserve `SlackOperationResult`
  shape + the `MentorshipEvent` local log; thin-client mapping test.
