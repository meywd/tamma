# Story 38-3: Slack / Notifications Step Mediation (Class D)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer responsible for the rule-1 "steps never call external APIs directly" invariant**,
I want `Integration/SlackActivity` re-pointed from the co-hosted, Slack-token-holding `IIntegrationService` to an internal **`POST /api/v1/notifications/slack`** endpoint that takes the post intent, holds the Slack token in `Tamma.Api`, performs the transport out-of-band (the outbox pattern), and audits it,
So that **a workflow step never holds or transits the Slack credential in the engine process** (closing the last `VIOLATION-by-co-hosting` token-holder in the Class-D row of the design §1.2 audit table) — even when the engine runs as per-tenant dedicated compute (the Cranl path), where the token would otherwise have to be pushed into the engine.

## Priority

P2 — Slack is the **low-blast-radius** rule-1 violator (design §1.3: "Token-holding but reads no tenant data"). It is not a data-exfiltration vector like the Class-A git writes (38-1) or the Class-C agent-dispatch (38-2), but it is still a **`VIOLATION-by-co-hosting`**: `SlackActivity` injects `IIntegrationService`, whose Slack-posting implementation reads the Slack token. That implementation is registered in `Tamma.Api` and is **unregistered/null in the engine**, so today it only "works" because the single-process deploy co-hosts the two — exactly the co-hosting that rule 1 forbids. This story removes the token from the engine's reach permanently and sets the **outbox pattern as the template for fire-and-forget external effects**, which the forward-looking Class-E (Stripe/billing, Epic 35) tie-in then inherits by design.

## Context

### What exists today (the violation-by-co-hosting)

`Tamma.Activities/Integration/SlackActivity.cs` is a `CodeActivity<SlackOperationResult>` that injects **`IIntegrationService`** (`Tamma.Core.Interfaces`) — the composite integration service whose concrete implementation holds the Slack bot token. The activity branches on an `Input<SlackAction>` (`SendChannel`, `SendDirect`, `SendAssessment`, `SendGuidance`, `SendNotification`) and calls `_integrationService.SendSlackMessageAsync(channel, message)` / `SendSlackDirectMessageAsync(userId, message)` **inside the engine process**. Per the design §1.2 audit table:

| Activity | External target | How it reaches it today | Holds/transits a key in-engine? | Verdict |
|---|---|---|---|---|
| `Integration/SlackActivity` | Slack | `IIntegrationService` (impl + Slack token in API; unregistered in engine) | only if co-hosted | **VIOLATION-by-co-hosting (low blast radius)** |

> **Co-hosting is NOT compliance (design §1.1).** Resolving a credential-holding service from the same DI container is allowed only by accident of the current single-process deploy. Rule 1 requires the step to call an internal endpoint **over the wire** via `TammaApiClient`, never to resolve an injected vendor service. The moment the engine runs as per-tenant dedicated compute (Cranl), the co-hosted `IIntegrationService` impl is **null** in the engine — and the Slack token must NOT be pushed there to "fix" it.

### What this story does (rule 1, Class D — the outbox variant)

This story applies the **outbox pattern** (the design §1.1 reference, embodied by `QueueWelcomeEmailActivity`) to the Slack effect, because a Slack post is a **fire-and-forget** external effect with no return value the workflow needs to branch on:

1. A new internal endpoint **`POST /api/v1/notifications/slack`** (`Tamma.Api/Endpoints/NotificationEndpoints.cs`), engine-only, authenticated on the **same plane** as the other `TammaApiClient` callbacks (Bearer `Tamma:ApiToken` via the engine auth handler + `X-Tenant-Id`). The endpoint writes the post **intent** to a control-plane **`slack_outbox`** table and returns **202 Accepted** — it does NOT call Slack synchronously.
2. A new out-of-band sender **`OutboxSlackSender`** (in `Tamma.Api`, mirroring `OutboxSmtpSender`) **holds the Slack token**, scans `slack_outbox` for `pending` rows, performs the actual Slack HTTPS post (`chat.postMessage`), records delivery / `LastError`, and emits the DCB audit event.
3. `SlackActivity` collapses to a **thin client**: it maps its `Input<>` props into a `SlackNotificationRequest` and sends it via a **new `TammaApiClient.QueueSlackNotificationAsync(...)`** (following the existing `PostVoidAsync` + `AddTenantHeader` + `RecordHealthAsync` pattern). It **injects no `IIntegrationService`** and holds no token. Its `SlackOperationResult` reports "queued" (`WaitingForResponse=false`), preserving the activity's existing output contract for the 1-2 mentorship workflows that read it.

The Slack token, the post, the audit, and any retry now live **entirely in `Tamma.Api`**. The engine's `IIntegrationService` Slack registration is removed from the engine composition (the git/agent-dispatch members of that composite interface are handled by 38-1 / 38-2; this story owns only the Slack methods).

### Forward-looking: Class E (Billing / Stripe, Epic 35) — ENFORCE BY DESIGN

The same outbox-vs-endpoint shape this story establishes is the **mandatory pattern for Class E** (design §1.2 final row + §5.1 Class-E): when Epic 35 lands, a billing step **emits an intent** (`POST /api/v1/billing/...` or a `billing_outbox` row) and the API — which alone holds the **Stripe key** — performs the charge/invoice, meters it, and audits. **Calling Stripe from an activity is PROHIBITED at design time.** This story includes a non-implemented `## Forward-looking: Class E (Billing / Stripe) enforce-by-design` subsection so the pattern is set **before** Epic 35 writes a single line, and so 38-4's guardrail analyzer (which this story is a sibling of) already covers a future `BillingActivity` that tries to inject a Stripe client.

### Explicitly out of scope (sibling stories / future epics)

- **Class A — git platform mediation** (`CreateBranch`/`CreatePullRequest`/`MergePullRequest`/`UpdateIssueStatus`/`AnalyzeReview` → `/api/v1/git/...`) is **38-1**.
- **Class C — agent-dispatch mediation** (`DispatchAgentWorkflow`/`MonitorAgentWorkflow`/`CollectAgentResults` → `/api/v1/agent-dispatch/...`) is **38-2**.
- **The build-time guardrail analyzer** that fails the build if any `Tamma.Activities` class re-introduces a direct `HttpClient`/vendor-service call is **38-4** (this story's effect is one of the violations 38-4 permanently protects).
- **Actual Stripe/billing implementation** is **Epic 35**; this story only fixes the pattern in stone (the Class-E subsection is documentation-only).
- **The LLM path** (`/api/v1/llm/call`) is Epic 32 (**32-5**); this story reuses its mediation template, not its endpoint.

## Acceptance Criteria

1. **The endpoint exists and is engine-only.** `POST /api/v1/notifications/slack` is served by a new `Tamma.Api/Endpoints/NotificationEndpoints.cs`, authenticated by **Bearer `Tamma:ApiToken` (engine auth handler) + `X-Tenant-Id`** — the same plane as the existing `TammaApiClient` callbacks (agent-resolve / budget / diagnostics / provider-session / `llm/call`). Missing/invalid bearer → **HTTP 401**. The handler binds a `SlackNotificationRequest`, derives `tenantId` from `X-Tenant-Id` when the body omits it, and persists an outbox row.

2. **The endpoint writes intent, not transport (the outbox pattern).** The endpoint **never calls Slack synchronously**. It validates the request, inserts a `slack_outbox` row (`Status="pending"`), and returns **HTTP 202 Accepted** with `{ outboxId }`. The Slack token is **not read** in the request path. (Mirrors `QueueWelcomeEmailActivity` → `platform_email_outbox` + `OutboxSmtpSender`, design §1.1 reference pattern.)

3. **`SlackNotificationRequest` matches the activity's surface.** The wire record carries `{ tenantId?, action ("SendChannel"|"SendDirect"|"SendAssessment"|"SendGuidance"|"SendNotification"), channel?, userId?, message, messageType ("Info"|"Warning"|"Success"|"Error"|"Celebration"), sessionId?, correlationId }`. Server-side message formatting (the emoji-prefix + assessment/guidance templating currently in `SlackActivity.FormatMessage`/`SendAssessmentRequest`/`SendGuidanceMessage`) **moves into the API** so the engine carries no presentation logic and posts no raw token-bearing call.

4. **`OutboxSlackSender` holds the token and performs the post out-of-band.** A new `Tamma.Api/Services/Notifications/OutboxSlackSender.cs` (a hosted/background scanner mirroring `OutboxSmtpSender`) reads the Slack bot token from configuration (`Slack:BotToken`), scans `slack_outbox` for `pending` rows whose `NextAttemptAt <= now`, posts to the Slack Web API (`chat.postMessage` for a channel / opening a DM for a user), and on success marks `Status="sent"` + `SentAt`. On a transient failure it sets `LastError`, increments `Attempts`, and backs off `NextAttemptAt`; on terminal failure (`Attempts >= MaxAttempts`) it marks `Status="failed"`. **The token is read only here, never returned to the engine, never logged.**

5. **`SlackActivity` becomes a thin client holding no token.** `SlackActivity` is reduced to map its `Input<SlackAction> Action` / `Channel` / `UserId` / `Message` / `MessageType` / `SessionId` props into a `SlackNotificationRequest` and send it via the **new `TammaApiClient.QueueSlackNotificationAsync(request, tenantId, ct)`** (returning `bool`, via `PostVoidAsync`). It **no longer injects `IIntegrationService`** and contains **no** `SendSlackMessageAsync`/`SendSlackDirectMessageAsync` call, no token, no Slack HTTP. Its `SlackOperationResult` reports `Success` = the queue result and `WaitingForResponse=false` (queued, fire-and-forget), preserving the output variable the mentorship workflows read. The `MentorshipEvent` session logging it does today stays — it is a local repository write, not an external call.

6. **The engine holds no Slack token after cutover.** The engine's `IIntegrationService` **Slack-method registration** is removed from the engine DI composition; `SlackActivity` resolves no Slack-credential-holding service. A `grep` over `Tamma.Activities` for `IIntegrationService` Slack usage / a Slack token / `chat.postMessage` / `slack.com` returns **zero** non-`TammaApiClient` hits. (38-4's analyzer makes this permanent.)

7. **DCB audit from the API, never the engine.** The post is audited from **`Tamma.Api`** (where the token + tenant store live): `NOTIFICATION.SLACK.QUEUED.SUCCESS` (from the endpoint, tags `{ action, channel?, userId?, sessionId?, correlationId, tenantId|userId }`) and exactly one terminal `NOTIFICATION.SLACK.SENT.SUCCESS` or `NOTIFICATION.SLACK.SENT.FAILED` (from `OutboxSlackSender`, the latter additionally tagging a key-free `failureReason`). The **message body is recorded as a redacted/length-bounded preview**, never raw secrets; the **token never appears** in any event payload.

8. **New control-plane table is registered in the destructive DROP list + the model contract test.** `slack_outbox` is a **control-plane / public-schema** table (it must deliver regardless of tenant-DB routing — same rationale as `platform_email_outbox`). It MUST be appended to `Program.cs`'s startup-reset "Wiping Tamma-managed public-schema tables" DROP list (else a 2nd test-host boot fails with `relation already exists`), and the new CP entity MUST be added to `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`'s strict `BeEquivalentTo` list. It is **not** a tenant-schema (`t_<hex>`) table.

9. **Exactly-once-per-intent on replay.** Because Elsa replays activities, `SlackActivity` calling the endpoint twice for the same `(correlationId, action, target)` must enqueue **one** row. The endpoint de-duplicates on `(correlationId, action, channel|userId)` via a partial unique index `WHERE Status <> 'failed'` (mirroring `EnqueueWelcomeOnceAsync`'s `(TenantId, Template) WHERE Status <> 'failed'` + in-code pre-check). A replayed enqueue returns the existing `outboxId`, inserting nothing.

10. **Fail-soft like the existing activity.** Today `SlackActivity` catches and returns `SlackOperationResult{Success=false}` rather than failing the workflow (a missing Slack post must not break a mentorship session). The thin client preserves this: an unreachable API / non-2xx response → `Success=false`, logged, workflow continues. (Consistent with `TammaApiClient`'s null-on-failure convention.)

11. **Forward-looking Class-E pattern documented (no code).** The story carries a `## Forward-looking: Class E (Billing / Stripe) enforce-by-design` subsection stating that a future billing step MUST emit an intent (`POST /api/v1/billing/...` or a `billing_outbox` row) and that calling Stripe from an activity is prohibited at design time — so Epic 35 inherits this shape and 38-4's analyzer already guards it. **No Stripe/billing code is written in this story.**

12. **Tests cover endpoint + outbox + thin-client + audit + dedup.** Endpoint auth (401 missing bearer); 202 + outbox-row-written + Slack-not-called-synchronously; `OutboxSlackSender` happy path (token read, post performed, `sent`) and failure/backoff (`LastError`, `Attempts`, terminal `failed`); the thin `SlackActivity` maps props → request and writes the same `SlackOperationResult` shape with `WaitingForResponse=false`; fail-soft on API-down; replay dedup (one row for two enqueues); one terminal `NOTIFICATION.SLACK.SENT.*` event; and the token never appears in any response/log/event (credential-safety assertion).

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  NotificationEndpoints.cs              # NEW — POST /api/v1/notifications/slack; engine-only auth; writes slack_outbox row; returns 202

apps/tamma-elsa/src/Tamma.Api/Services/Notifications/
  SlackNotificationRequest.cs           # NEW — wire request record (AC3)
  ISlackNotificationService.cs          # NEW — enqueue + format seam (called by the endpoint)
  SlackNotificationService.cs           # NEW — formats message (moved from SlackActivity), de-dupes, inserts outbox row
  OutboxSlackSender.cs                  # NEW — out-of-band background sender; HOLDS the Slack token; chat.postMessage; audits

apps/tamma-elsa/src/Tamma.Data/Entities/
  SlackOutboxMessage.cs                 # NEW — CP outbox entity (mirrors PlatformEmailOutboxMessage)

apps/tamma-elsa/src/Tamma.Data/Repositories/
  ISlackOutboxRepository.cs             # NEW — EnqueueOnceAsync + ClaimPendingAsync + MarkSent/MarkFailed
  SlackOutboxRepository.cs              # NEW — partial-unique-index-backed exactly-once enqueue

apps/tamma-elsa/src/Tamma.Activities/Integration/
  SlackActivity.cs                      # GUT — drop IIntegrationService; thin client over TammaApiClient.QueueSlackNotificationAsync

apps/tamma-elsa/src/Tamma.Activities/LlmCall/
  TammaApiClient.cs                     # MODIFY — add QueueSlackNotificationAsync(SlackNotificationRequest, tenantId, ct) (PostVoidAsync pattern)

apps/tamma-elsa/src/Tamma.ElsaServer/
  Program.cs                            # MODIFY — remove the engine's Slack IIntegrationService registration (Slack methods only)
  Program.cs                            # MODIFY — append "slack_outbox" to the public-schema DROP list (AC8)

apps/tamma-elsa/src/Tamma.Api/
  Program.cs                            # MODIFY — map NotificationEndpoints; register ISlackNotificationService + OutboxSlackSender (hosted); EF model for SlackOutboxMessage

apps/tamma-elsa/src/Tamma.Data/Migrations/
  <timestamp>_AddSlackOutbox.cs         # NEW — amends the existing snapshot (sequential; does NOT branch it)
```

### The endpoint (`NotificationEndpoints.cs`)

```csharp
// POST /api/v1/notifications/slack — internal, engine-only. Same auth plane as the other TammaApiClient callbacks.
app.MapPost("/api/v1/notifications/slack", async (
        SlackNotificationRequest request,
        HttpContext http,
        ISlackNotificationService notifications,
        CancellationToken ct) =>
{
    // Bearer validated by the engine auth scheme; missing/invalid -> 401 before this runs.
    var tenantId = request.TenantId ?? ResolveTenant(http);     // from X-Tenant-Id when body omits it

    // Writes intent ONLY — never calls Slack here. The token is NOT read in this path.
    var outboxId = await notifications.EnqueueAsync(request with { TenantId = tenantId }, ct);  // de-dupes (AC9)

    // NOTIFICATION.SLACK.QUEUED.SUCCESS emitted inside EnqueueAsync (tenant IEventRepository).
    return Results.Accepted($"/api/v1/notifications/slack/{outboxId}", new { outboxId });        // 202
})
.RequireAuthorization(EngineAuthPolicy)   // same plane as agent-resolve / budget / llm/call
.WithName("QueueSlackNotification");
```

### `SlackNotificationRequest` (the wire contract — AC3)

```csharp
public sealed record SlackNotificationRequest
{
    public Guid? TenantId { get; init; }            // null => single-user/platform; also from X-Tenant-Id
    public required string Action { get; init; }    // SendChannel|SendDirect|SendAssessment|SendGuidance|SendNotification
    public string? Channel { get; init; }           // for channel posts
    public string? UserId { get; init; }            // for DMs
    public required string Message { get; init; }    // raw content; formatting applied server-side
    public string MessageType { get; init; } = "Info";  // Info|Warning|Success|Error|Celebration
    public Guid? SessionId { get; init; }           // mentorship session context (logged, not transmitted to Slack)
    public required string CorrelationId { get; init; } // workflow instance id — drives dedup + audit
}
```

### The CP outbox entity (`SlackOutboxMessage.cs`) — mirrors `PlatformEmailOutboxMessage`

```csharp
public sealed class SlackOutboxMessage
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }      // XOR with UserId, same discipline as platform_email_outbox
    public Guid? UserId { get; set; }
    public string Action { get; set; } = null!;     // SendChannel|SendDirect|...
    public string? Channel { get; set; }
    public string? UserHandle { get; set; }         // Slack user id for DMs (NOT a Tamma UserId)
    public string FormattedMessage { get; set; } = null!;  // already emoji/templated server-side
    public Guid? SessionId { get; set; }
    public string CorrelationId { get; set; } = null!;
    public string Status { get; set; } = "pending"; // pending|sent|failed
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }          // key-free
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? SentAt { get; set; }
}
// Partial unique index: (CorrelationId, Action, COALESCE(Channel, UserHandle)) WHERE Status <> 'failed'  (AC9)
```

### `OutboxSlackSender` (holds the token; out-of-band; AC4/AC7)

```csharp
// Mirrors OutboxSmtpSender: a background scanner in Tamma.Api. THE token-holder.
public sealed class OutboxSlackSender   // BackgroundService / hosted scanner
{
    private readonly string _botToken;          // from Slack:BotToken — read ONLY here
    private readonly ISlackOutboxRepository _repo;
    private readonly IHttpClientFactory _http;
    private readonly IEventRepository _events;  // tenant-scoped audit

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var row in await _repo.ClaimPendingAsync(batch: 25, ct))
        {
            try
            {
                await PostToSlackAsync(row, _botToken, ct);   // chat.postMessage / DM-open — the ONLY Slack call
                await _repo.MarkSentAsync(row.Id, ct);
                await _events.AppendAsync(SlackSent(row), ct);          // NOTIFICATION.SLACK.SENT.SUCCESS
            }
            catch (Exception ex)
            {
                await _repo.MarkFailedAsync(row.Id, KeyFree(ex), ct);   // backoff or terminal 'failed'
                await _events.AppendAsync(SlackFailed(row, KeyFree(ex)), ct); // NOTIFICATION.SLACK.SENT.FAILED
            }
        }
    }
}
```

### The thin `SlackActivity` shim (AC5/AC10)

```csharp
// No IIntegrationService. No token. No Slack HTTP. Maps props -> request -> queue.
var req = new SlackNotificationRequest {
    TenantId = tenantId, Action = action.ToString(),
    Channel = channel, UserId = userId, Message = message,
    MessageType = messageType.ToString(), SessionId = sessionId,
    CorrelationId = context.WorkflowExecutionContext.Id
};
var queued = await _api.QueueSlackNotificationAsync(req, tenantId?.ToString(), ct);  // NEW client method (PostVoidAsync)

if (sessionId.HasValue && queued)
    await _repository!.LogEventAsync(/* MentorshipEvent — local write, kept */);

context.SetResult(new SlackOperationResult {
    Success = queued,
    Message = queued ? "Notification queued" : "Notification queue failed",
    Destination = channel ?? userId ?? "unknown",
    WaitingForResponse = false        // fire-and-forget; nothing to await
});
// Fail-soft (AC10): queued==false does NOT throw; the workflow continues.
```

### `TammaApiClient.QueueSlackNotificationAsync` (AC5)

```csharp
public Task<bool> QueueSlackNotificationAsync(
    SlackNotificationRequest request, string? tenantId = null, CancellationToken ct = default)
{
    var url = $"{_baseUrl}/api/v1/notifications/slack";
    return PostVoidAsync(url, request, tenantId, ct);   // AddTenantHeader + RecordHealthAsync, null/false on failure
}
```

## Forward-looking: Class E (Billing / Stripe) enforce-by-design

> **Documentation-only in this story. No Stripe/billing code is written here.** This subsection fixes the pattern in stone *before* Epic 35 lands, so billing inherits the same mediation shape this story establishes for Slack and 38-4's analyzer already guards it.

Per design §1.2 (final row, "Enforce by design") and §5.1 (Class E):

- **A workflow step MUST NOT call Stripe (or any billing vendor) directly.** A billing step emits an **intent** — either `POST /api/v1/billing/...` (request/response, like `/llm/call`) for operations the workflow must branch on, or a **`billing_outbox` row** (fire-and-forget, like this story's `slack_outbox`) for charges/invoices that can be performed out-of-band.
- **`Tamma.Api` alone holds the Stripe key.** It performs the charge/invoice, meters it (Epic 35 / 36), and audits it. The engine never holds the Stripe key, never hits `api.stripe.com`.
- **Outbox vs endpoint by effect shape:** a synchronous "is this card valid / what is the price" → endpoint; an asynchronous "charge this usage / send this invoice" → `billing_outbox` + an `OutboxStripeSender` mirroring this story's `OutboxSlackSender`.
- **38-4 already covers it:** a future `BillingActivity` that injects a `StripeClient` or POSTs to `api.stripe.com` from `Tamma.Activities` would **fail the build** under 38-4's guardrail analyzer. This is the cheapest possible enforcement: the violation can never compile.

When Epic 35 is authored, its billing-effect stories cite this subsection as the binding pattern.

## Dependencies

**Internal (siblings / prerequisites):**

- **38-1** (git platform mediation) — sibling Class-A mediation; both re-point `Tamma.Activities` activities to `TammaApiClient` and both deregister members of the same co-hosted `IIntegrationService` composite (38-1 owns the GitHub methods; this story owns the Slack methods). Coordinate the engine-side deregistration so neither leaves a dangling registration.
- **38-2** (agent-dispatch mediation) — sibling Class-C mediation; same `TammaApiClient`/endpoint template.
- **38-4** (build-time guardrail analyzer) — the permanent backstop; this story's cutover is one of the violations 38-4 protects. Land 38-4's allowlist *after* this story's `QueueSlackNotificationAsync` exists so `TammaApiClient` is on the allowed host list.
- **32-5** (`POST /api/v1/llm/call`) — the mediation **template** this story copies (engine-only auth plane, `TammaApiClient` callback convention, DCB-from-the-API, `feedback_resolution_no_empty_fallback` discipline). Not a runtime dependency.
- **Epic 28 / 29** (control-plane outbox + cabinet) — `slack_outbox` is a CP table reusing the `platform_email_outbox` + `OutboxSmtpSender` pattern; the Slack `Slack:BotToken` is a platform credential (single Slack workspace today; per-tenant Slack BYOK is a future extension via the Epic 29 cabinet, not in this story).

**Reference patterns (compliant exemplars — design §1.1):**

- `QueueWelcomeEmailActivity` + `platform_email_outbox` + `OutboxSmtpSender` — the **outbox** template this story mirrors for fire-and-forget effects.
- `TriggerCIActivity` — the engine-callback template (POSTs to an internal endpoint, holds no vendor credential).
- `TammaApiClient` — the engine→API HTTP delegation seam (Bearer `Tamma:ApiToken` + `X-Tenant-Id`, `PostVoidAsync`/`AddTenantHeader`/`RecordHealthAsync`).

**Consumers (downstream, not blockers):**

- **Epic 36** (analytics) / **Epic 37** (audit) — consume `NOTIFICATION.SLACK.*` events.
- **Epic 35** (billing) — inherits the enforce-by-design Class-E pattern documented above.

**External:** Slack Web API (`chat.postMessage`) — now reached **only** from `OutboxSlackSender` in `Tamma.Api`, never from the engine.

## Testing Strategy

1. **Endpoint auth.** Missing/invalid bearer → 401; valid bearer + `X-Tenant-Id` → request bound, `tenantId` derived from header when body omits it.
2. **Endpoint writes intent, returns 202 (AC2).** A valid request inserts exactly one `slack_outbox` row (`Status="pending"`) and returns 202 + `{ outboxId }`; assert (via a fake/spy on the Slack HTTP path) that **Slack is NOT called synchronously** and the token is **not read** in the request path.
3. **Server-side formatting (AC3).** `SendAssessment`/`SendGuidance`/`MessageType` produce the same emoji-prefixed/templated body the legacy `SlackActivity.FormatMessage` produced — assert the formatted body is persisted, not the raw message.
4. **`OutboxSlackSender` happy path (AC4/AC7).** A `pending` row → token read from `Slack:BotToken`, `chat.postMessage` performed (faked HTTP), row → `sent` + `SentAt`, one `NOTIFICATION.SLACK.SENT.SUCCESS`.
5. **`OutboxSlackSender` failure/backoff (AC4).** Transient post failure → `LastError` set (key-free), `Attempts++`, `NextAttemptAt` backed off, `Status` stays `pending`; after `MaxAttempts` → `Status="failed"` + one `NOTIFICATION.SLACK.SENT.FAILED`.
6. **Thin-client mapping (AC5).** `SlackActivity` maps `Action`/`Channel`/`UserId`/`Message`/`MessageType`/`SessionId` → `SlackNotificationRequest` and writes `SlackOperationResult{ Success=queued, WaitingForResponse=false }`; the `MentorshipEvent` session log still fires on success.
7. **Fail-soft (AC10).** `QueueSlackNotificationAsync` returns false (API down) → `SlackOperationResult{Success=false}`, no throw, workflow continues.
8. **Replay dedup (AC9).** Two enqueues with the same `(correlationId, action, target)` → one `slack_outbox` row; the second returns the existing `outboxId`. Partial unique index `WHERE Status <> 'failed'` enforced at the DB level.
9. **Engine holds no token (AC6).** `grep` over `Tamma.Activities` for `IIntegrationService` Slack methods / `chat.postMessage` / `slack.com` / a Slack token → zero non-`TammaApiClient` hits; `SlackActivity` constructor injects no `IIntegrationService`.
10. **CP-table registration (AC8).** A 2nd test-host boot succeeds (no `relation already exists`) — proves `slack_outbox` is in the DROP list; `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` includes `SlackOutboxMessage`.
11. **Credential safety (AC7).** Assert the Slack token never appears in any `slack_outbox` row, `LastError`, log line, HTTP response, or DCB event payload; the message body in events is redacted/length-bounded.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

3-4 days (one endpoint + CP outbox entity/repo/migration + out-of-band sender + the thin-client cutover of a single activity + the DROP-list/model-test registration). Lower than 38-1/38-2 (one activity, fire-and-forget, no cross-tenant authorization decision).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/NotificationEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Notifications/SlackNotificationRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Notifications/ISlackNotificationService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Notifications/SlackNotificationService.cs` | Create (formatting moved from `SlackActivity`; dedup; enqueue) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Notifications/OutboxSlackSender.cs` | Create (token-holder; out-of-band; audit) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/SlackOutboxMessage.cs` | Create (CP outbox entity) |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/ISlackOutboxRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/SlackOutboxRepository.cs` | Create (partial-unique-index exactly-once) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/<ts>_AddSlackOutbox.cs` | Create (amends existing snapshot) |
| `apps/tamma-elsa/src/Tamma.Activities/Integration/SlackActivity.cs` | Gut → thin client; drop `IIntegrationService` |
| `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` | Modify (add `QueueSlackNotificationAsync`) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (remove engine Slack registration; append `slack_outbox` to DROP list) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map endpoint; register service + `OutboxSlackSender`; EF model) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Notifications/NotificationEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Notifications/OutboxSlackSenderTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/Integration/SlackActivityThinClientTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (add `SlackOutboxMessage`) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback` and the outbox-pattern notes around Story 28-5/28-6).
3. Read the design of record §1 (steps never call providers), §1.2 (the Class-D Slack audit row), §5.1 (Class-D + Class-E endpoints), and §5.2 (the follow-up-epic phasing) IN FULL.
4. Reviewed `QueueWelcomeEmailActivity.cs` + `PlatformEmailOutboxMessage` + `OutboxSmtpSender` (the outbox template you are copying), `SlackActivity.cs` (the activity you are gutting + the formatting you are moving), and `TammaApiClient` (the callback pattern + `PostVoidAsync`).
5. Confirmed with 38-1's author who deregisters which methods of the shared `IIntegrationService` composite so the engine has no dangling Slack/GitHub registration.
6. Planned the TDD approach — write the endpoint-202/no-sync-call test and the dedup test first.

### Key Design Decisions

- **Outbox, not request/response (deliberate).** A Slack post is fire-and-forget with no value the workflow branches on, so it follows `QueueWelcomeEmailActivity` (intent → out-of-band sender) rather than `/llm/call` (synchronous mediation). This also makes the post resilient to the Slack API being briefly down — the row stays `pending` and retries — which the synchronous shape could not give cheaply.
- **The token lives in `OutboxSlackSender` only.** The endpoint writes intent and never reads `Slack:BotToken`; the sender is the single token-holder. This is the design §1.1 invariant: credential-holding code lives only in `Tamma.Api`.
- **Server-side formatting.** The emoji/assessment/guidance templating moves out of the engine so the engine carries no presentation coupling to Slack and the message is fully resolved before it touches the token-holding path.
- **CP table, not tenant table.** `slack_outbox` is control-plane (must deliver regardless of tenant-DB routing — same rationale as `platform_email_outbox`), so it goes in the `Program.cs` DROP list and the CP model contract test, **not** the per-tenant `EfTenantDbMigrator`.
- **Sequential migration.** This story amends the existing EF snapshot (one `AddSlackOutbox` migration); it does not branch it. 38-1/38-2/38-3/38-4 are implemented sequentially per the EF parallel-migration hazard.
- **Class-E is design-only here.** The Stripe pattern is fixed in stone via the subsection above so Epic 35 inherits it and 38-4 guards it — but no billing code ships in this story.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of a Slack notification? | The sole user (keyed by `UserId`; `TenantId` may be null). | The tenant (keyed by `TenantId` from `X-Tenant-Id`). No per-user layer. |
| Whose Slack credential performs the post? | The platform `Slack:BotToken` (single workspace today); per-tenant Slack BYOK is a future Epic-29-cabinet extension, not this story. | Same platform `Slack:BotToken`; if/when per-tenant Slack BYOK lands it resolves from the tenant cabinet — never pushed to the engine. |
| Where does the `slack_outbox` row's owner scoping land? | `UserId` set, `TenantId` null (XOR, like `platform_email_outbox`). | `TenantId` set, `UserId` null. |
| Where do `NOTIFICATION.SLACK.*` events land? | The user's (sole) tenant event store. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`. Never cross-tenant. |
| Who owns the notification audit data? | The user. | The tenant — platform admin sees none of it (design ownership rule). |
| Who may deregister/manage the Slack integration? | The sole user (it is their instance). | Platform-owner (`PlatformOwnerAccess`) for the platform workspace; tenant cannot mint a new bot token. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Engine still resolves a Slack-token service after cutover (AC6) | High | Remove the engine's Slack `IIntegrationService` registration; `grep` `Tamma.Activities` for Slack methods / `chat.postMessage` → zero; 38-4's analyzer makes it permanent. |
| New CP table breaks the 2nd test-host boot (AC8) | High | Append `slack_outbox` to the `Program.cs` "Wiping Tamma-managed public-schema tables" DROP list **and** to `ControlPlaneDbContextModelTests`'s strict list; the 2nd-boot test proves it. |
| Replay double-posts (AC9) | Medium | Partial unique index `(CorrelationId, Action, target) WHERE Status <> 'failed'` + in-code pre-check (mirrors `EnqueueWelcomeOnceAsync`); dedup test. |
| Token leaks into a row / event / log (AC7) | High | Token read only in `OutboxSlackSender`; `LastError` is key-free; events carry a redacted/length-bounded message preview; credential-safety test asserts zero token occurrences. |
| Output-contract drift breaks the mentorship workflows that read `SlackOperationResult` (AC5) | Medium | Preserve the `SlackOperationResult` shape (`Success`/`Message`/`Destination`/`WaitingForResponse=false`); keep the `MentorshipEvent` local log; thin-client mapping test. |
| Slack briefly down loses the notification | Low | Outbox `pending` + backoff retry (the reason for choosing the outbox shape over synchronous mediation). |
| Dangling shared-composite registration with 38-1 | Medium | Coordinate the `IIntegrationService` deregistration split (Slack here, GitHub in 38-1) before either lands. |

### Success Metrics

- [ ] `grep` over `Tamma.Activities` finds **zero** Slack-token / `chat.postMessage` / Slack `IIntegrationService` usage (the engine holds no Slack credential).
- [ ] 100% of Slack effects go through `POST /api/v1/notifications/slack` → `slack_outbox` → `OutboxSlackSender`; the endpoint never calls Slack synchronously.
- [ ] Every delivered post produces one `NOTIFICATION.SLACK.QUEUED.SUCCESS` + exactly one terminal `NOTIFICATION.SLACK.SENT.*`.
- [ ] The 2nd test-host boot succeeds and `ControlPlaneDbContextModelTests` is green (CP table registered).
- [ ] The Class-E enforce-by-design subsection is present so Epic 35 inherits the pattern (verified by 38-4's coverage of a hypothetical `BillingActivity`).

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1 steps-never-call-providers; §1.2 audit table — Class-D Slack row + Class-E billing row; §5.1 Class-D/Class-E endpoints; §5.2 follow-up-epic phasing)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-38-3-slack-notifications-step-mediation-plan.md`
- Sibling stories: `story-38-1/` (git platform mediation), `story-38-2/` (agent-dispatch mediation), `story-38-4/` (build-time guardrail analyzer); `docs/stories/epic-32/story-32-5/` (the `/llm/call` mediation template)
- Forward tie-in: **Epic 35** (billing) — inherits the Class-E enforce-by-design pattern documented here.
- Reference patterns: `apps/tamma-elsa/src/Tamma.Activities/TenantLifecycle/QueueWelcomeEmailActivity.cs` + `Tamma.Data/Entities/PlatformEmailOutboxMessage.cs` + `OutboxSmtpSender` (outbox); `apps/tamma-elsa/src/Tamma.Activities/Testing/TriggerCIActivity.cs` (engine-callback); `apps/tamma-elsa/src/Tamma.Activities/LlmCall/TammaApiClient.cs` (callback seam)
- Code being changed: `apps/tamma-elsa/src/Tamma.Activities/Integration/SlackActivity.cs` (the activity being gutted)

## Logging Requirements

- **INFO**: slack-notification queued (correlationId, action, channel/userId target, sessionId, tenantId — **never the raw token**); outbox sender delivered (outboxId, action, target, durationMs); engine thin-client queue result (success/false).
- **DEBUG**: server-side message formatting (action → formatted-body length, not the secret-bearing content verbatim), dedup hit (existing `outboxId` returned on replay), outbox claim/batch size.
- **WARN**: queue failure (API unreachable → `SlackOperationResult{Success=false}`, fail-soft), outbox post transient failure (`Attempts`, backoff `NextAttemptAt`, key-free `LastError`).
- **ERROR**: outbox terminal failure (`Status="failed"` after `MaxAttempts`), DCB append failure (the post still completes; the append failure is logged, not swallowed), and any attempt by the engine to resolve a Slack-credential service (guardrail — should be impossible after cutover).
- **Structured context**: `{ correlationId, action, channel?, userId?, sessionId?, tenantId|userId, outboxId, status }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist the Slack bot token (`Slack:BotToken`), raw Slack auth headers, or webhook secrets. The token is read **only** inside `OutboxSlackSender`. `LastError` is sanitized to be key-free; event payloads carry a **redacted, length-bounded** message preview (never the raw message if it may contain secrets); the `slack_outbox` row, HTTP responses, and all DCB events are token-free by contract.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — Class-D Slack/notifications step mediation. Re-points `Integration/SlackActivity` from the co-hosted `IIntegrationService` (Slack token in API, unregistered in engine) to `POST /api/v1/notifications/slack`, adopting the outbox pattern (`slack_outbox` CP table + `OutboxSlackSender` token-holder, mirroring `QueueWelcomeEmailActivity`/`platform_email_outbox`/`OutboxSmtpSender`) for the fire-and-forget post; thin-client `SlackActivity` over a new `TammaApiClient.QueueSlackNotificationAsync`; CP DROP-list + model-contract-test registration; DCB audit from the API; never-log-token credential safety; and a forward-looking Class-E (Billing/Stripe, Epic 35) enforce-by-design subsection (documentation-only) so the mediation pattern is set before Epic 35 lands. Sibling of 38-1/38-2 (Class A/C) and protected by 38-4's guardrail analyzer. | Claude |
