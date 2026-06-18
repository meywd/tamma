# Story 35-5: Stripe Webhook Ingestion, Idempotency & Billing Event Projection

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), the `.dev/` knowledge base usage rules, TRACE/DEBUG logging requirements, the Test-Driven Development workflow, and the build/coverage quality gates. Failure to follow this process will result in rework.

## User Story

As a **Tamma platform operator**,
I want a verified, idempotent Stripe webhook endpoint that ingests subscription/invoice/payment lifecycle events, projects them onto the control-plane billing mirrors, and fans them into the DCB event stream,
So that the local control plane stays in sync with Stripe as the source of truth for billing state, replays and retries are safe, and every billing state change leaves a complete audit trail.

## Priority

P0 - Backbone for the entire billing epic. Stories 35-4 (subscription lifecycle), 35-7 (payment methods / portal), 35-8 (invoicing & dunning), and 35-10 (credits wallet) all depend on this webhook ingestion + projection seam to learn about Stripe-side state changes.

## Acceptance Criteria

1. `POST /api/v1/billing/stripe/webhook` is an anonymous-but-signature-gated endpoint (no JWT/API-key auth; verification is the Stripe signature) that captures the **raw** request body via `HttpRequest.EnableBuffering()` + a leave-open `StreamReader` (mirroring `GitHubEndpoints.Webhooks` in `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs`), then validates it with `Stripe.EventUtility.ConstructEvent(rawBody, stripeSignatureHeader, signingSecret)`.
2. The webhook **signing secret** is resolved through `ISecretStore.GetAsync(...)` at `SecretScope.Platform` / `SecretPurpose.ApiKey` exactly as Story 35-1 wires it (NOT a raw `IConfiguration` read). When the secret is unresolvable the endpoint returns `503` and logs ERROR — it never fails open, matching `GitHubEndpoints` audit finding 001 ("never fail open on missing secret").
3. An invalid or missing signature returns `400 Bad Request`, no row is written, no event is emitted, and a WARN log is recorded with the Stripe event id (when parseable) — never the raw body.
4. A new control-plane entity `BillingWebhookEvent` (`apps/tamma-elsa/src/Tamma.Data/Entities/BillingWebhookEvent.cs`, registered on `ControlPlaneDbContext` and configured in `TammaModelConfiguration`) dedupes deliveries on a `UNIQUE` index over `StripeEventId`; columns include `Id`, `StripeEventId`, `EventType`, `TenantId` (nullable — resolved from the Stripe customer), `Status` (`received|processing|projected|enqueued|failed|skipped`), `Attempts`, `Payload` (raw JSON), `LastError`, `ReceivedAt`, `ProcessedAt`.
5. A duplicate delivery (same `StripeEventId`) is acknowledged `200 OK` **without** reprocessing — the unique-index insert collision is caught and treated as an idempotent ack, so Stripe at-least-once retries never double-project.
6. Inbound events are mapped to a tenant via `BillingCustomer.StripeCustomerId → BillingCustomer.TenantId` (the Story 35-1 entity); the resolved `TenantId` is stamped on the `BillingWebhookEvent` row and on every emitted DCB event's `tenantId` tag. An event whose customer maps to no `BillingCustomer` is acknowledged `200`, recorded `Status = skipped`, and logged WARN (no exception, no Stripe retry storm).
7. Handlers are dispatched through a pluggable `IBillingEventHandler` registry (`HandledEventTypes` + `HandleAsync(BillingWebhookContext, ct)`), so sibling stories register their own projection logic without 35-5 owning their entities. This story ships the dispatch seam plus default handlers covering at minimum: `customer.subscription.created/updated/deleted`, `invoice.created/finalized/paid/payment_failed`, `payment_intent.succeeded/payment_intent.payment_failed`, `customer.subscription.trial_will_end`, `charge.dispute.created`.
8. Each successfully dispatched event emits a corresponding `BILLING.*` DCB event via `IEventRepository.AppendAsync(DomainEvent)` (CP store), named `AGGREGATE.ACTION.STATUS` (e.g. `BILLING.SUBSCRIPTION.UPDATED`, `BILLING.INVOICE.PAID`, `BILLING.PAYMENT.FAILED`, `BILLING.DISPUTE.OPENED`, `BILLING.SUBSCRIPTION.TRIAL_ENDING`) with JSONB `tags = { tenantId, stripeEventId, eventType, stripeObjectId }`.
9. Mirror **updates** owned by sibling stories (e.g. `BillingSubscription` in 35-4, `BillingInvoice` in 35-8, `BillingPaymentMethod` in 35-7) are performed by **their** registered `IBillingEventHandler` implementations; 35-5 only guarantees the dispatch + DCB emission + a `NullBillingEventHandler`/logging default so the pipeline is testable standalone. 35-5 does **not** create `BillingSubscription`, `BillingInvoice`, or `BillingPaymentMethod` entities.
10. The handler is **fast-ack**: the endpoint verifies → dedupes → projects the cheap mirror/DCB write inline, but any heavy follow-up (dunning escalation, email, Stripe round-trips) is enqueued as a `PlatformQueuedTask` (`Type = "billing.webhook.followup"`) via `IPlatformQueuedTaskRepository.EnqueueAsync`, never run inline. p95 ack latency `< 2s`.
11. Unhandled/unknown event types (no `IBillingEventHandler` matches) are logged at INFO, recorded `Status = skipped`, and acknowledged `200` — the endpoint returns no `4xx`/`5xx` that would trigger Stripe retry storms (only signature failure → `400`, secret-unresolvable → `503`).
12. An admin replay/inspect endpoint `GET /api/v1/admin/billing/webhook-events` (policy `PlatformOwnerAccess`) lists recent `BillingWebhookEvent` rows with processing status, filterable by `status`, `eventType`, `tenantId`, paged (default 50, max 200); `POST /api/v1/admin/billing/webhook-events/{id}/replay` (`PlatformOwnerAccess`) re-dispatches a stored payload through the same processor (idempotent — re-running a `projected` event is a no-op at the handler level).
13. In **single-user mode** (`ITammaModeProvider` → `SingleUser`, no `Tamma:TenantSharedSecret`/SaaS markers) the webhook route and admin replay route are **not mapped** and the `NullBillingProvider` seam from Story 35-1 means no Stripe wiring is registered — matching 35-1 AC7. In **SaaS mode** the routes are mapped and tenant resolution is mandatory.
14. Unit + integration tests cover: signature verify pass/fail, secret-unresolvable `503`, dedupe on replay (one row, one projection, one event), each default handler's DCB emission + dispatch, the fast-ack `PlatformQueuedTask` enqueue path, unknown-event-type `200 skipped`, no-customer-match `200 skipped`, admin list/replay RBAC, and tenant-isolation (a webhook for tenant A never writes a DCB event tagged tenant B).

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/Tamma.Data/Entities/
  BillingWebhookEvent.cs                       # NEW — dedup + audit row (CP)

apps/tamma-elsa/src/Tamma.Data/
  ControlPlaneDbContext.cs                      # MODIFY — DbSet<BillingWebhookEvent>
  TammaModelConfiguration.cs                    # MODIFY — entity config + unique index
  Migrations/ControlPlane/<ts>_BillingWebhookEvents.cs   # NEW — additive migration

apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/
  StripeWebhookEndpoint.cs                      # NEW — raw-body capture + verify + ack
  BillingWebhookAdminEndpoints.cs              # NEW — list + replay (PlatformOwnerAccess)

apps/tamma-elsa/src/Tamma.Api/Services/Billing/
  StripeWebhookProcessor.cs                     # NEW — dedupe + dispatch + DCB + enqueue
  IStripeWebhookProcessor.cs                    # NEW — processor seam (testable)
  IBillingEventHandler.cs                       # NEW — pluggable handler contract
  BillingEventHandlerRegistry.cs               # NEW — type → handler resolution
  BillingWebhookContext.cs                      # NEW — Stripe.Event + tenant + raw payload
  NullBillingEventHandler.cs                   # NEW — logging default (no mirror)
  BillingWebhookEventTypes.cs                  # NEW — BILLING.* DCB type constants
  Handlers/                                     # NEW — 35-5's default DCB-emitting handlers
    SubscriptionWebhookHandler.cs              # emits BILLING.SUBSCRIPTION.* (mirror in 35-4)
    InvoiceWebhookHandler.cs                   # emits BILLING.INVOICE.*     (mirror in 35-8)
    PaymentWebhookHandler.cs                   # emits BILLING.PAYMENT.*     (method mirror 35-7)
    DisputeWebhookHandler.cs                   # emits BILLING.DISPUTE.OPENED
  BillingWebhookFollowupTaskHandler.cs         # NEW — IPlatformTaskHandler for fast-ack work

apps/tamma-elsa/src/Tamma.Api/Extensions/
  BillingWebhookServiceCollectionExtensions.cs # NEW — DI wiring (mode-gated)

apps/tamma-elsa/src/Tamma.Api/Program.cs        # MODIFY — map routes (SaaS-mode-gated)
```

> **Stripe.net** is added to `Tamma.Api.csproj` by Story 35-1 (foundation). 35-5 consumes `Stripe.EventUtility`, `Stripe.Event`, and the typed `Stripe.Subscription` / `Stripe.Invoice` / `Stripe.PaymentIntent` / `Stripe.Charge` event objects already on the dependency.

### Entity sketch — `BillingWebhookEvent`

```csharp
namespace Tamma.Data.Entities;

/// <summary>
/// Control-plane dedup + audit row for an inbound Stripe webhook delivery.
/// Stripe delivers at-least-once; the UNIQUE index on StripeEventId makes
/// reprocessing safe — a duplicate insert collision is treated as an
/// idempotent ack. Mirrors the GitHubWebhookDelivery audit-row pattern.
/// </summary>
public class BillingWebhookEvent
{
    public Guid Id { get; set; }
    public string StripeEventId { get; set; } = null!;   // UNIQUE — "evt_..."
    public string EventType { get; set; } = null!;       // "invoice.paid"
    public Guid? TenantId { get; set; }                  // resolved from BillingCustomer
    public string? StripeObjectId { get; set; }          // "sub_...", "in_...", "pi_..."
    public string Status { get; set; } = "received";     // received|processing|projected|enqueued|failed|skipped
    public int Attempts { get; set; }
    public string Payload { get; set; } = "{}";          // raw JSON (redacted of nothing PII-sensitive beyond Stripe's own)
    public string? LastError { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
```

### EF migration sketch (additive — `dotnet ef migrations add BillingWebhookEvents`)

```sql
CREATE TABLE billing_webhook_events (
    id                UUID PRIMARY KEY,
    stripe_event_id   TEXT NOT NULL,
    event_type        TEXT NOT NULL,
    tenant_id         UUID NULL REFERENCES tenants(id),
    stripe_object_id  TEXT NULL,
    status            TEXT NOT NULL DEFAULT 'received',
    attempts          INTEGER NOT NULL DEFAULT 0,
    payload           JSONB NOT NULL DEFAULT '{}',
    last_error        TEXT NULL,
    received_at       TIMESTAMPTZ NOT NULL,
    processed_at      TIMESTAMPTZ NULL
);
CREATE UNIQUE INDEX ux_billing_webhook_events_stripe_event_id
    ON billing_webhook_events (stripe_event_id);
CREATE INDEX ix_billing_webhook_events_status_received
    ON billing_webhook_events (status, received_at DESC);
CREATE INDEX ix_billing_webhook_events_tenant
    ON billing_webhook_events (tenant_id) WHERE tenant_id IS NOT NULL;
```

Entity config lives only in `TammaModelConfiguration.ConfigureControlPlaneEntities` (the established single source — same place `AlertRule`/`GitHubWebhookDelivery` indexes are declared). After adding, run `dotnet ef migrations has-pending-model-changes` → expect none.

### Handler contract — `IBillingEventHandler`

```csharp
namespace Tamma.Api.Services.Billing;

public interface IBillingEventHandler
{
    /// Stripe event types this handler claims (e.g. "invoice.paid").
    IReadOnlyCollection<string> HandledEventTypes { get; }

    /// Project the event onto local mirrors + emit the BILLING.* DCB event.
    /// Must be idempotent: re-running an already-projected event is a no-op.
    /// Returns the queued follow-up task payload (or null for none).
    Task<BillingFollowup?> HandleAsync(BillingWebhookContext ctx, CancellationToken ct);
}

public sealed record BillingWebhookContext(
    Stripe.Event StripeEvent,
    Guid TenantId,
    string RawPayload);

public sealed record BillingFollowup(string TaskType, string Payload);
```

The `BillingEventHandlerRegistry` mirrors `PlatformTaskHandlerRegistry` exactly: a per-scope snapshot dict keyed by event type, duplicate-claim detection at construction, `Resolve(eventType) -> handler?`. Sibling stories register their handlers via `services.AddBillingEventHandler<T>()`; an unclaimed type falls through to `NullBillingEventHandler` (logs INFO, `Status = skipped`).

### Processor signature — `StripeWebhookProcessor`

```csharp
public interface IStripeWebhookProcessor
{
    /// Verify-already-done caller passes the parsed Stripe.Event + raw body.
    /// Returns the ack result (always 200 unless caller already rejected sig).
    Task<WebhookProcessResult> ProcessAsync(
        Stripe.Event stripeEvent, string rawPayload, CancellationToken ct);
}
```

`ProcessAsync` flow:
1. Insert `BillingWebhookEvent { Status = received }`. On `DbUpdateException` from the unique index → return `Duplicate` (idempotent ack, no further work).
2. Resolve `TenantId` from `BillingCustomer` by the event's customer id. No match → `Status = skipped`, INFO/WARN log, return `Skipped`.
3. `registry.Resolve(eventType)` → handler (or `NullBillingEventHandler`). Run `HandleAsync` (mirror write + `IEventRepository.AppendAsync` of the `BILLING.*` DCB event) inside the CP transaction.
4. If handler returned a `BillingFollowup`, `IPlatformQueuedTaskRepository.EnqueueAsync(new PlatformQueuedTask { Type = "billing.webhook.followup", TenantId, Payload })`; stamp `Status = enqueued`, else `Status = projected`; set `ProcessedAt`.
5. Any handler exception → `Status = failed`, `LastError` (scrubbed via `CredentialRedactor.Clean`), still ack `200` (Stripe replay is covered by our own dedup; we use the admin replay endpoint + follow-up queue for recovery rather than relying on Stripe retries, avoiding retry storms). The thrown error is logged ERROR.

### Endpoint shape

```
POST /api/v1/billing/stripe/webhook            (SaaS only; signature-gated, no JWT)
GET  /api/v1/admin/billing/webhook-events      (PlatformOwnerAccess; filters + paging)
POST /api/v1/admin/billing/webhook-events/{id}/replay   (PlatformOwnerAccess)
```

`StripeWebhookEndpoint.Receive` mirrors `GitHubEndpoints.Webhooks`:

```csharp
public static async Task<IResult> Receive(
    HttpContext context,
    [FromServices] ISecretStore secrets,
    [FromServices] IStripeWebhookProcessor processor,
    [FromServices] ILoggerFactory loggerFactory)
{
    var logger = loggerFactory.CreateLogger("StripeWebhookEndpoint");
    var sig = context.Request.Headers["Stripe-Signature"].FirstOrDefault();
    if (string.IsNullOrEmpty(sig)) return Results.BadRequest(new { error = "missing signature" });

    context.Request.EnableBuffering();
    string raw;
    using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8,
        detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
    {
        raw = await reader.ReadToEndAsync();
        context.Request.Body.Position = 0;
    }

    var signingSecret = await ResolveSigningSecretAsync(secrets); // Platform/ApiKey
    if (signingSecret is null) { logger.LogError("Stripe webhook secret unresolvable"); return Results.StatusCode(503); }

    Stripe.Event evt;
    try { evt = Stripe.EventUtility.ConstructEvent(raw, sig, signingSecret); }
    catch (Stripe.StripeException) { logger.LogWarning("Stripe webhook signature rejected"); return Results.BadRequest(); }

    var result = await processor.ProcessAsync(evt, raw, context.RequestAborted);
    return Results.Ok(new { received = true, status = result.Status });
}
```

### DCB event names (`BillingWebhookEventTypes`)

```
BILLING.SUBSCRIPTION.CREATED   BILLING.SUBSCRIPTION.UPDATED   BILLING.SUBSCRIPTION.DELETED
BILLING.SUBSCRIPTION.TRIAL_ENDING
BILLING.INVOICE.CREATED        BILLING.INVOICE.FINALIZED      BILLING.INVOICE.PAID
BILLING.INVOICE.PAYMENT_FAILED
BILLING.PAYMENT.SUCCEEDED      BILLING.PAYMENT.FAILED
BILLING.DISPUTE.OPENED
BILLING.WEBHOOK.SKIPPED        BILLING.WEBHOOK.FAILED         (operational, system event source)
```

All appended via `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags, Metadata, Data })` with `Tags` JSON `{ tenantId, stripeEventId, eventType, stripeObjectId }` and `Metadata` `{ "workflowVersion": "1.0.0", "eventSource": "system" }`.

### Per-mode + per-tenant handling

| Concern | single-user mode | SaaS mode |
|---|---|---|
| Webhook route mapped? | No — `NullBillingProvider` (35-1) means no Stripe; route + admin route unmapped. | Yes. |
| Tenant resolution | N/A | Mandatory: `BillingCustomer.StripeCustomerId → TenantId`; unresolved → `skipped`. |
| DCB event `TenantId` | N/A | Always the resolved tenant; never null for a projected event. |
| Admin replay/list | N/A | `PlatformOwnerAccess` (platform-admin claim only) — never a tenant-scoped route; a Stripe webhook is platform-operator concern. |
| Mode source | `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`) — process-stable. | same |

Tenant isolation is enforced two ways: (1) every `DomainEvent` is tagged with the resolved `TenantId` and written through the CP `IEventRepository` with that tenant id; (2) the admin endpoints are platform-scoped (no tenant route exists), so there is no path for a tenant to read another tenant's webhook rows.

### Integration points

- **Story 35-1 foundation** — `BillingCustomer` (tenant ↔ Stripe customer mapping), `IBillingProvider`/`NullBillingProvider` seam, Stripe.net package, and the `ISecretStore` resolution of the Stripe webhook signing secret (`SecretScope.Platform`, `SecretPurpose.ApiKey`).
- **Secret cabinet (Epic 29)** — `ISecretStore.GetAsync` for the signing secret; never an env-var read.
- **DCB event store** — `IEventRepository.AppendAsync` (CP `domain_events`), the same store `AlertRuleEvaluator` polls, so a future `BILLING.*` alert rule (Epic 5/23) sees these for free.
- **Platform task queue (Epic 28)** — `IPlatformQueuedTaskRepository.EnqueueAsync` + a new `IPlatformTaskHandler` (`TaskType = "billing.webhook.followup"`) processed by the existing `PlatformTaskWorker`.
- **Sibling stories** — 35-4/35-7/35-8/35-10 register `IBillingEventHandler` implementations that own their mirror entities.

## Dependencies

**Internal:**
- **Prerequisite — Story 35-1**: `BillingCustomer`, `IBillingProvider`/`NullBillingProvider`, Stripe.net package, signing-secret-via-`ISecretStore` wiring, single-user no-op seam.
- **Blocks — Story 35-4** (subscription lifecycle registers `SubscriptionWebhookHandler` mirror update; 35-4 declares a dependency on 35-5).
- **Blocks — Story 35-7** (payment methods / portal consumes payment-method webhook events).
- **Blocks — Story 35-8** (invoicing & dunning registers invoice/dunning handlers + `billing.webhook.followup` work).
- **Blocks — Story 35-10** (credits wallet reacts to invoice/payment webhooks).
- **Related — Epic 28**: `PlatformQueuedTask` + `PlatformTaskWorker` fast-ack queue.
- **Related — Epic 29**: secret cabinet for the signing secret.
- **Related — Epic 5/23**: DCB events feed the alert evaluator / analytics.

**External:**
- **Stripe.net** SDK (added by 35-1) — `EventUtility.ConstructEvent`, typed event objects.
- A configured Stripe **webhook endpoint signing secret** (`whsec_...`) stored in the cabinet.
- `STRIPE_SECRET_KEY_TEST` + a Stripe test fixture/CLI for integration tests.

## Testing Strategy

**Unit tests** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/`, xUnit; SQLite/in-memory CP context, Stripe SDK behind a thin seam or fixture-string `ConstructEvent`):
1. **Signature**: a fixture raw body + valid `Stripe-Signature` (computed with a test `whsec_`) verifies and processes; a tampered body / wrong secret → `400`, zero rows, zero events.
2. **Secret unresolvable**: `ISecretStore.GetAsync` returns null → endpoint returns `503`, no processing (never fails open).
3. **Dedupe**: process the same `evt_...` twice → exactly one `BillingWebhookEvent` row, one DCB event, one handler invocation; second call returns `Duplicate`/`200` with no new event.
4. **Tenant resolution**: customer maps to tenant A → row + DCB event tagged tenant A; customer maps to nothing → `Status = skipped`, `200`, INFO/WARN, no DCB projection event.
5. **Per-handler**: for each default handler, a representative Stripe event → correct `BILLING.*` DCB type emitted with `{ tenantId, stripeEventId, eventType, stripeObjectId }` tags; idempotent re-run is a no-op.
6. **Unknown type**: `foo.bar.baz` → `NullBillingEventHandler`, `Status = skipped`, INFO, `200`, no `BILLING.*` projection event.
7. **Fast-ack enqueue**: a handler returning a `BillingFollowup` → one `PlatformQueuedTask` (`Type = "billing.webhook.followup"`, correct `TenantId`/`Payload`) enqueued; `Status = enqueued`.
8. **Handler failure**: handler throws → `Status = failed`, `LastError` scrubbed, still `200` (no Stripe retry storm), ERROR logged.
9. **Registry**: duplicate event-type claim across two handlers throws at construction (mirrors `PlatformTaskHandlerRegistry`).

**Integration tests** (`Tamma.Api.Tests/Billing/` via `WebApplicationFactory`, docker-bound CP Postgres run as `sg docker -c "dotnet test ..."`):
10. **End-to-end ack**: POST a CLI-generated signed event → `200`, row persisted, DCB event readable via `IEventRepository`.
11. **Tenant isolation**: two `BillingCustomer` rows (tenant A, tenant B) + interleaved webhooks → every DCB event/row carries the correct tenant; a query scoped to tenant A never returns tenant B's webhook events.
12. **Admin RBAC**: `GET /webhook-events` and `POST .../replay` → `403` for non-platform-admin, `200` for `PlatformOwnerAccess`; replay re-dispatches a stored payload and a re-run of a `projected` event is a no-op.
13. **Mode gating**: with single-user markers the webhook + admin routes return `404` (unmapped); with SaaS markers they are reachable.

**Mocks/fixtures**: Stripe is never live in unit tests — use static fixture JSON bodies + a deterministic `whsec_` for `ConstructEvent`, or a thin `IStripeEventVerifier` seam so the SDK is swappable. `ISecretStore`, `IEventRepository`, `IPlatformQueuedTaskRepository`, and `IBillingEventHandler` are mocked/faked. Integration tests require `STRIPE_SECRET_KEY_TEST` and use the Stripe CLI `stripe trigger` (or recorded fixtures) — skip-gated when the env var is absent.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/BillingWebhookEvent.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<BillingWebhookEvent>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config + unique/secondary indexes) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_BillingWebhookEvents.cs` | Create (additive) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/StripeWebhookEndpoint.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/Billing/BillingWebhookAdminEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IStripeWebhookProcessor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/StripeWebhookProcessor.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingEventHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingEventHandlerRegistry.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingWebhookContext.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/NullBillingEventHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingWebhookEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/SubscriptionWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/InvoiceWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/PaymentWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/Handlers/DisputeWebhookHandler.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Billing/BillingWebhookFollowupTaskHandler.cs` | Create (`IPlatformTaskHandler`) |
| `apps/tamma-elsa/src/Tamma.Api/Extensions/BillingWebhookServiceCollectionExtensions.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map routes, SaaS-mode-gated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/StripeWebhookProcessorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/StripeWebhookEndpointTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingEventHandlerRegistryTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/BillingWebhookAdminEndpointsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (especially the GitHub webhook audit findings 001 + 020 referenced in `GitHubEndpoints.cs`)
3. Confirmed Story 35-1 is merged (Stripe.net, `BillingCustomer`, signing-secret-via-`ISecretStore`, `NullBillingProvider`)
4. Read `Stripe.net` webhook docs (`EventUtility.ConstructEvent`) — do NOT hand-roll HMAC; the SDK handles the `t=`/`v1=` scheme + timestamp tolerance
5. Planned the TDD Red-Green-Refactor cycle (tests first per the table above)

### Key Design Decisions

- **Pluggable handler seam over monolithic switch.** The spec lists mirror updates for subscription/invoice/payment, but those entities are owned by 35-4/35-7/35-8. 35-5 ships the *dispatch + DCB emission* and a `NullBillingEventHandler` default; sibling stories register `IBillingEventHandler` for their entities. This honors the epic's story boundaries and lets 35-5 ship + be tested before 35-4/35-8 land.
- **Dedup at the unique index, not a pre-SELECT.** Insert-then-catch on `UNIQUE(stripe_event_id)` is race-safe under concurrent Stripe retries (same pattern as `GitHubWebhookDelivery (PlatformKind, DeliveryId)`); a pre-check has a TOCTOU window.
- **Never `4xx`/`5xx` except signature/secret.** Unknown types, no-customer-match, and handler errors all ack `200` and record status locally — Stripe retries on non-2xx and a storm of retries on a permanently-unprojectable event would be self-inflicted DoS. Recovery is our own admin-replay endpoint + the follow-up queue.
- **Verify with raw bytes.** `EnableBuffering()` + leave-open `StreamReader` then reset `Body.Position = 0` so model binding (if any) still works — copied verbatim from `GitHubEndpoints.Webhooks`. Any middleware that re-serializes the body would break signature verification, so the route stays body-untouched until after verify.
- **Secret from the cabinet, never env.** Matches 35-1 AC3 + GitHub audit finding 001 (never fail open). Unresolvable secret → `503`, not silent acceptance.
- **Fast-ack.** Inline work is bounded to one CP transaction (dedup row + mirror + DCB event); everything heavy goes on `PlatformQueuedTask`. Target p95 ack `< 2s`.

### Boundary note (epic ownership)

35-5 does **not** create or migrate `BillingSubscription` (35-4), `BillingInvoice`/`BillingInvoiceLine` (35-8), `BillingPaymentMethod` (35-7), `BillingUsageRollup` (35-3), or `BillingWalletLedger` (35-10). It owns only `BillingWebhookEvent`, the webhook endpoint, the processor, the handler-dispatch seam + DCB emission, the admin replay/inspect endpoint, and the follow-up `PlatformQueuedTask` type. If a sibling story's handler is not yet registered, the corresponding event projects to its `BILLING.*` DCB event and acks `200` via the `NullBillingEventHandler` (DCB audit trail is complete even before the mirror handler exists).

### Graceful degradation

- CP DB write failure on the dedup row → ERROR log, return `503` so Stripe retries (this is the one case where retry is *wanted* — we never persisted the event). Distinct from handler failure, which we own and recover via replay.
- Single-user / `NullBillingProvider`: route unmapped, zero Stripe surface.

## Logging Requirements

- **INFO**: webhook received (`stripeEventId`, `eventType`, `tenantId`), event projected/enqueued/skipped (with reason), admin list/replay invoked.
- **DEBUG**: handler resolution (`eventType → handlerType`), dedup-hit (`stripeEventId`), follow-up task enqueued (`taskType`).
- **WARN**: signature rejected (`stripeEventId` if parseable — never the raw body), no `BillingCustomer` match (`stripeCustomerId`), unknown event type.
- **ERROR**: signing secret unresolvable, CP dedup-row write failure, handler exception (`stripeEventId`, scrubbed `lastError`).
- **Structured context**: include `{ stripeEventId, eventType, tenantId, status, attempts }` where applicable.
- **Credential safety**: NEVER log the signing secret, the `Stripe-Signature` header, Stripe API keys, raw card/PII fields, or the raw webhook body. Scrub `LastError`/`finalError` through `CredentialRedactor.Clean` before persistence.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
