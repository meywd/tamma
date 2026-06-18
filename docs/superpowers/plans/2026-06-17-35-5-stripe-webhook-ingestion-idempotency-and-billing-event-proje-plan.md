# Story 35-5 — Stripe Webhook Ingestion, Idempotency & Billing Event Projection (Implementation Plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. Story: `docs/stories/epic-35/story-35-5/35-5-stripe-webhook-ingestion-idempotency-and-billing-event-proje.md`.

**Goal:** Ship a verified, idempotent Stripe webhook endpoint on `Tamma.Api` (SaaS mode) that
ingests subscription/invoice/payment/dispute lifecycle events, dedupes them by Stripe event id,
dispatches each through a pluggable `IBillingEventHandler` seam, emits a canonical `BILLING.*` DCB
event per event, fast-acks (heavy work → `PlatformQueuedTask`), and exposes an admin
replay/inspect surface. This is the source-of-truth sync backbone the rest of Epic 35 builds on.

**Tech stack:** .NET 8 / EF Core 8 / Npgsql in `apps/tamma-elsa` (control-plane API). Stripe.net
arrives via Story 35-1. Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Billing/` (xUnit;
docker-bound CP-Postgres suites run via `sg docker -c "dotnet test ..."`).

---

## Non-goals (YAGNI guard)

- NO ownership of sibling-story mirror entities. `BillingSubscription` (35-4),
  `BillingInvoice`/`BillingInvoiceLine` (35-8), `BillingPaymentMethod` (35-7),
  `BillingUsageRollup` (35-3), `BillingWalletLedger` (35-10) are created by their stories. 35-5
  ships the dispatch seam + DCB emission + a logging `NullBillingEventHandler` default; the actual
  mirror writes are sibling handlers registered later.
- NO Stripe-side resource creation (customers, subscriptions, prices, meters). 35-1 owns
  customer/catalog creation; 35-4 owns subscription mutations. 35-5 only *receives* Stripe's view.
- NO inline heavy work (dunning escalation, email sends, Stripe round-trips). Those are
  `PlatformQueuedTask` (`Type = "billing.webhook.followup"`) processed by the existing
  `PlatformTaskWorker`.
- NO single-user billing surface. In `SingleUser` mode the routes are unmapped and the
  `NullBillingProvider` (35-1) means zero Stripe wiring.
- NO tenant-facing webhook route. A Stripe webhook is a platform-operator concern; the only read
  surface is `PlatformOwnerAccess`-gated admin endpoints.
- NO reliance on Stripe's retry for recovery of *projection* failures. We ack `200` and recover via
  our own admin-replay endpoint + the follow-up queue, to avoid retry storms.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### Webhook + signature pattern to mirror

- `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitHubEndpoints.cs:109-170` — `Webhooks(...)` is the
  canonical raw-body-capture + verify-before-dispatch pattern: `context.Request.EnableBuffering()`
  (line 125) → leave-open `StreamReader` (127-136) → `Body.Position = 0`. **Audit finding 001**
  (138-149): *never fail open on a missing secret* — reject when the secret is unset. We copy this
  shape exactly but use `Stripe.EventUtility.ConstructEvent` instead of hand-rolled HMAC, and
  resolve the secret from the cabinet (not `IConfiguration`).
- `VerifySignature` (274-282) shows the timing-safe HMAC the SDK does for us — do **not**
  re-implement; `Stripe.net` handles the `t=`/`v1=` scheme + tolerance window.

### Story 35-1 foundation this story consumes (verified via `/tmp/pab_stories/35-1.json`)

- `apps/tamma-elsa/src/Tamma.Data/Entities/BillingCustomer.cs` — `TenantId` (unique FK),
  `StripeCustomerId`, `BillingMode` enum (`PlatformProvided|Byok`). This is the tenant-resolution
  table: `StripeCustomerId → TenantId`.
- `apps/tamma-elsa/src/Tamma.Api/Services/Billing/IBillingProvider.cs` +
  `StripeBillingProvider.cs` + `NullBillingProvider` (single-user no-op seam, 35-1 AC7).
- Stripe API key + webhook signing secret resolve via `ISecretStore`
  (`SecretScope.Platform`, `SecretPurpose.ApiKey`) — 35-1 AC3; production refuses to boot billing
  if the key is only a raw env var. **35-5 reuses this resolution path for the signing secret.**
- Stripe.net package added to `Tamma.Api.csproj` by 35-1 (it is **not** present today — confirmed
  no `Stripe` ref in `apps/tamma-elsa/src/Tamma.Api/Tamma.Api.csproj`).

### Secret cabinet (Epic 29)

- `apps/tamma-elsa/src/Tamma.Api/Services/Secrets/ISecretStore.cs` — `GetAsync(SecretRef, ct)`.
  Note: `ISecretStore` does **not** return plaintext through its public signature (the doc-comment
  is explicit). 35-1 establishes the concrete read path for the Stripe key/secret; 35-5 must call
  whatever 35-1 exposes for plaintext resolution of the signing secret (likely a billing-scoped
  helper on `StripeBillingProvider` / a reveal path). **Implementation task 5 reads 35-1's actual
  surface and binds to it; do not assume `ISecretStore.GetAsync` returns the plaintext.**
- `SecretScope.Platform` (`Services/Secrets/SecretScope.cs:26`) and `SecretPurpose.ApiKey`
  (`Services/Secrets/SecretPurpose.cs:29`) exist.

### DCB event store

- `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` — `Id`, `Type`, `TenantId`,
  `IssueNumber`, `Tags` (JSONB string), `Metadata`, `Data`, `CreatedAt`, `SequenceNumber`.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` —
  `Task<DomainEvent> AppendAsync(DomainEvent evt)` (CP-resident; the store `AlertRuleEvaluator`
  polls). `AlertEventEmitter.cs:31-50` shows the tenant-vs-platform routing precedent and the
  `CredentialRedactor.Clean` scrub-before-persist rule.

### Platform task queue (Epic 28)

- `apps/tamma-elsa/src/Tamma.Data/Entities/PlatformQueuedTask.cs` — `Id`, `Type`, `TenantId`,
  `InstallationId`, `Payload`, `Status`, retry/claim fields.
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IPlatformQueuedTaskRepository.cs:29` —
  `EnqueueAsync(PlatformQueuedTask, ct)`; usage example at
  `Endpoints/Admin/AdminTenantsEndpoints.cs:700` (`MoveTenantTaskPayload.TaskType` pattern).
- `apps/tamma-elsa/src/Tamma.Api/Services/PlatformTasks/IPlatformTaskHandler.cs` — `TaskType` +
  `HandleAsync(PlatformQueuedTask, ct)`; `PlatformTaskTerminalException` for non-retryable.
  `IPlatformTaskHandlerRegistry.cs` shows the per-scope snapshot-dict + duplicate-type detection
  pattern we mirror for `BillingEventHandlerRegistry`. Register via
  `services.AddPlatformTaskHandler<T>()`.

### DbContext / model config / migrations

- `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs:36-78` — existing
  `DbSet<Tenant>`/`Plan`/`PlatformQueuedTask`. Add `DbSet<BillingWebhookEvent>` here.
- `ControlPlaneDbContext.cs:218` calls
  `TammaModelConfiguration.ConfigureControlPlaneEntities(...)` — entity config + indexes belong in
  `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` (the established single source; e.g.
  `GitHubWebhookDelivery` unique index at `ControlPlaneDbContext.cs:269-271` is the dedup-index
  precedent, but declare ours in `TammaModelConfiguration`).
- Migrations under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/` —
  `BillingWebhookEvent` is an **additive** table (normal `dotnet ef migrations add`); after, run
  `dotnet ef migrations has-pending-model-changes` → expect none.

### Authorization policies (`Program.cs`)

- `Program.cs:986-990` — `PlatformOwnerAccess` (requires JWT `platformRole = platform_admin`). Use
  for the admin list/replay routes — *not* `OwnerAccess` (per the policy comment at 976-985, admin
  platform-scoped work must use `PlatformOwnerAccess`).
- Route mapping precedent: `Program.cs:1244` `var admin = app.MapGroup("/api/admin")...`; anonymous
  webhook routes mapped without `RequireAuthorization` (GitHub webhook is mapped likewise).
- Mode gating: `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs:48`,
  `TammaMode.SingleUser|SaaS`). Map billing routes only when `SaaS`.

### Tests

- `apps/tamma-elsa/tests/Tamma.Api.Tests/` has per-area folders (`GitHub/`, `Alerts/`,
  `Provisioning/`, `TaskQueue/`, `Webhooks/`). Create a new `Billing/` folder. `WebApplicationFactory`
  end-to-end pattern exists (e.g. `Webhooks/`, `GitHub/`). Docker-bound CP-Postgres suites run as
  `sg docker -c "dotnet test apps/tamma-elsa/... "` (session docker group is stale; build needs no
  wrapper) — see `reference_dotnet_test_docker` memory.

---

## Architecture

**Receive → verify → dedupe → resolve tenant → dispatch → emit DCB → fast-ack enqueue → record.**

```
POST /api/v1/billing/stripe/webhook
  └─ StripeWebhookEndpoint.Receive
       ├─ EnableBuffering + raw-body read (mirror GitHubEndpoints.Webhooks)
       ├─ resolve signing secret via 35-1's cabinet path  → 503 if unresolvable
       ├─ Stripe.EventUtility.ConstructEvent(raw, sig, secret) → 400 on StripeException
       └─ IStripeWebhookProcessor.ProcessAsync(evt, raw)
            ├─ insert BillingWebhookEvent{received}; UNIQUE collision → Duplicate (200)
            ├─ resolve TenantId via BillingCustomer.StripeCustomerId; none → skipped (200)
            ├─ BillingEventHandlerRegistry.Resolve(evt.Type) ?? NullBillingEventHandler
            ├─ handler.HandleAsync(ctx) → mirror write (sibling) + IEventRepository.AppendAsync(BILLING.*)
            ├─ if BillingFollowup → IPlatformQueuedTaskRepository.EnqueueAsync(billing.webhook.followup)
            └─ stamp Status projected|enqueued|skipped|failed; always ack 200 (except sig/secret)
```

Sibling stories `services.AddBillingEventHandler<T>()`; 35-5's default handlers emit the canonical
`BILLING.*` DCB events even before sibling mirror handlers register, so the audit trail is complete
from day one.

---

## Task breakdown (TDD — tests first in every task)

### Task 1 — `BillingWebhookEvent` entity + EF migration (core data)

**Files:**
- New: `src/Tamma.Data/Entities/BillingWebhookEvent.cs` (per story entity sketch).
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` — `DbSet<BillingWebhookEvent>`.
- Modify: `src/Tamma.Data/TammaModelConfiguration.cs` — `ConfigureControlPlaneEntities`: table
  `billing_webhook_events`, `UNIQUE(stripe_event_id)`, `(status, received_at DESC)` index, partial
  `(tenant_id) WHERE NOT NULL` index, default `status='received'`/`attempts=0`/`payload='{}'`.
- New: migration under `src/Tamma.Data/Migrations/ControlPlane/<ts>_BillingWebhookEvents.cs`.

**Approach:** additive table only. Mirror `GitHubWebhookDelivery` config style. Generate via
`dotnet ef migrations add BillingWebhookEvents -c ControlPlaneDbContext`.

**Tests (first):** `Billing/BillingWebhookEventModelTests.cs` — context creates the table (in-memory
+ SQLite relational for index assertions); inserting two rows with the same `StripeEventId` throws
on the unique index; `has-pending-model-changes` reports none (CI guard).

### Task 2 — Handler seam: `IBillingEventHandler` + registry + `NullBillingEventHandler`

**Files:**
- New: `src/Tamma.Api/Services/Billing/IBillingEventHandler.cs`,
  `BillingEventHandlerRegistry.cs`, `BillingWebhookContext.cs`, `BillingFollowup` (record),
  `NullBillingEventHandler.cs`, `BillingWebhookEventTypes.cs`.

**Approach:** clone `PlatformTaskHandlerRegistry` structure — per-scope snapshot dict keyed by
event type, duplicate-claim throws at construction, `Resolve(type) -> handler?`. `NullBillingEventHandler`
logs INFO + returns null follow-up; the processor uses it when `Resolve` is null. Define an
`AddBillingEventHandler<T>()` DI helper.

**Tests (first):** `Billing/BillingEventHandlerRegistryTests.cs` — resolves a registered handler by
type; returns null for unclaimed type; two handlers claiming the same type throw at construction;
`NullBillingEventHandler` returns a null follow-up and emits no projection.

### Task 3 — `StripeWebhookProcessor` (dedupe + dispatch + DCB + enqueue)

**Files:**
- New: `src/Tamma.Api/Services/Billing/IStripeWebhookProcessor.cs`, `StripeWebhookProcessor.cs`,
  `WebhookProcessResult` (record/enum).

**Approach:** the flow in the Architecture section. Insert-then-catch on the unique index for
dedupe (race-safe). Tenant resolve via `BillingCustomer`. Append `BILLING.*` via
`IEventRepository.AppendAsync` with tags `{ tenantId, stripeEventId, eventType, stripeObjectId }`
and metadata `{ workflowVersion: "1.0.0", eventSource: "system" }`. Follow-up → `PlatformQueuedTask`.
Handler exception → `Status=failed`, `LastError` via `CredentialRedactor.Clean`, ack `200`. CP
dedup-row write failure → bubble so the endpoint returns `503` (the only case where Stripe retry is
wanted). All in one CP transaction for the inline path.

**Tests (first):** `Billing/StripeWebhookProcessorTests.cs` — dedupe (one row/event/handler call on
double-process); no-customer-match → `skipped`+`200`+no projection event; per default-handler DCB
type + tags; unknown type → `NullBillingEventHandler`+`skipped`; follow-up → one `PlatformQueuedTask`
with right `TenantId`/`Payload`+`Status=enqueued`; handler throw → `failed`+scrubbed `LastError`+`200`;
tenant-isolation (interleaved A/B → correct tenant on every row + event).

### Task 4 — Default handlers (subscription / invoice / payment / dispute)

**Files:**
- New: `src/Tamma.Api/Services/Billing/Handlers/SubscriptionWebhookHandler.cs`,
  `InvoiceWebhookHandler.cs`, `PaymentWebhookHandler.cs`, `DisputeWebhookHandler.cs`.

**Approach:** each claims its Stripe types (subscription: created/updated/deleted/trial_will_end;
invoice: created/finalized/paid/payment_failed; payment: payment_intent.succeeded/payment_failed;
dispute: charge.dispute.created), reads the typed `evt.Data.Object` (`Stripe.Subscription` etc.),
emits the matching `BILLING.*` DCB event, and returns a `BillingFollowup` ONLY for events that need
heavy follow-up (e.g. `invoice.payment_failed` → dunning follow-up). **No mirror writes here** —
those are sibling-story handlers (35-4/35-7/35-8); 35-5's handlers are DCB-emitting + idempotent.
Idempotency: re-dispatch of an already-`projected` row is a no-op (the processor short-circuits on
`Status=projected` during replay; handlers themselves stay side-effect-light).

**Tests (first):** extend `StripeWebhookProcessorTests` with fixture events per handler asserting the
exact `BILLING.*` type and that `payment_failed`/`dispute` enqueue a follow-up while `paid`/`created`
do not.

### Task 5 — Webhook endpoint (raw-body capture, verify, secret-from-cabinet, ack)

**Files:**
- New: `src/Tamma.Api/Endpoints/Billing/StripeWebhookEndpoint.cs`.

**Approach:** mirror `GitHubEndpoints.Webhooks` raw-body capture verbatim. **First, read 35-1's
actual signing-secret resolution surface** (`StripeBillingProvider` / cabinet helper) and bind to it
— do not assume `ISecretStore.GetAsync` returns plaintext (its doc-comment forbids that). Missing
`Stripe-Signature` → `400`; unresolvable secret → `503`+ERROR (never fail open);
`EventUtility.ConstructEvent` throws → `400`+WARN (no raw body in logs). On success delegate to
`IStripeWebhookProcessor.ProcessAsync` and `Results.Ok`.

**Tests (first):** `Billing/StripeWebhookEndpointTests.cs` (WebApplicationFactory) — valid signed
fixture → `200`+row+DCB event; tampered body → `400`+no row; missing signature → `400`;
secret-unresolvable → `503`; duplicate delivery → `200`+one row.

### Task 6 — Admin replay/inspect endpoints

**Files:**
- New: `src/Tamma.Api/Endpoints/Billing/BillingWebhookAdminEndpoints.cs`.

**Approach:** `GET /api/v1/admin/billing/webhook-events` (filters `status`/`eventType`/`tenantId`,
paging default 50/max 200) + `POST /api/v1/admin/billing/webhook-events/{id}/replay` (re-dispatch
the stored payload through `IStripeWebhookProcessor`; re-running a `projected` event is a no-op).
Both `PlatformOwnerAccess`.

**Tests (first):** `Billing/BillingWebhookAdminEndpointsTests.cs` — RBAC matrix (`403` non-platform-admin,
`200` platform-admin); list filters + paging; replay re-dispatches and a re-run of `projected` is a
no-op (no duplicate DCB event).

### Task 7 — Follow-up `IPlatformTaskHandler` + DI wiring + route mapping (mode-gated)

**Files:**
- New: `src/Tamma.Api/Services/Billing/BillingWebhookFollowupTaskHandler.cs`
  (`TaskType = "billing.webhook.followup"`).
- New: `src/Tamma.Api/Extensions/BillingWebhookServiceCollectionExtensions.cs` —
  `AddBillingWebhookIngestion()`: registers processor, registry, `NullBillingEventHandler`, the four
  default handlers, the follow-up `IPlatformTaskHandler`. Wired only when
  `ITammaModeProvider == SaaS`.
- Modify: `src/Tamma.Api/Program.cs` — call the extension; map the webhook + admin routes only in
  SaaS mode (single-user → unmapped).

**Approach:** mirror `AlertServiceCollectionExtensions` / `PlatformTaskServiceCollectionExtensions`.
The follow-up handler is a thin v1 (logs + acks); 35-8 dunning replaces its body — keep it minimal,
non-throwing for unknown follow-up subtypes.

**Tests (first):** mode-gating test — single-user markers → `POST /webhook` and admin routes `404`;
SaaS markers → reachable. Follow-up handler test — a `billing.webhook.followup` task is processed
without throwing.

---

## Sequencing & dependencies

```
Task 1 (entity/migration) ─┐
Task 2 (handler seam) ──────┼─► Task 3 (processor) ─► Task 4 (default handlers)
                            │                          │
                            └──────────────────────────┴─► Task 5 (endpoint) ─► Task 6 (admin) ─► Task 7 (DI + mode-gate)
```

- **Prerequisite:** Story 35-1 merged (Stripe.net, `BillingCustomer`, `NullBillingProvider`,
  signing-secret cabinet path). Task 5 is blocked on knowing 35-1's secret-resolution surface.
- Tasks 1 + 2 are parallel-safe. Task 3 needs both. Task 4 extends Task 3's tests. Tasks 5→6→7 are
  sequential (endpoint → admin → wiring/mode-gate).
- **Downstream:** 35-4/35-7/35-8/35-10 register `IBillingEventHandler` against this seam.

---

## Risks + mitigations

- **Stripe retry storm from non-2xx acks.** Mitigation: only signature (`400`) and unresolvable
  secret (`503`) are non-2xx; unknown types, no-customer-match, and handler failures all ack `200`
  and record local status. Recovery via admin-replay + follow-up queue, not Stripe retries.
- **Body consumed before signature verify.** Mitigation: `EnableBuffering()` + leave-open reader +
  `Body.Position = 0`, copied from `GitHubEndpoints.Webhooks`; route stays body-untouched until
  after verify; no JSON model binding on the route.
- **Secret resolution coupling to 35-1.** `ISecretStore` doc-comment forbids returning plaintext;
  Task 5 must bind to whatever concrete plaintext path 35-1 exposes. Mitigation: Task 5 starts by
  reading 35-1's `StripeBillingProvider`/cabinet helper; if 35-1 is not yet merged, stub behind a
  small `IStripeSigningSecretSource` seam and bind on integration.
- **Dedup race under concurrent retries.** Mitigation: insert-then-catch on `UNIQUE(stripe_event_id)`
  (no TOCTOU window), exactly the `GitHubWebhookDelivery (PlatformKind, DeliveryId)` precedent.
- **Owning a sibling's entity by accident.** Mitigation: explicit boundary — 35-5 creates only
  `BillingWebhookEvent`; mirror writes are sibling handlers; `NullBillingEventHandler` keeps the DCB
  trail complete before they register. Code review checks no `BillingSubscription`/`BillingInvoice`/
  `BillingPaymentMethod` entity is added here.
- **Event-store topology shift (Story 28-1 / Epic 30).** `BILLING.*` events append to the CP
  `domain_events` via `IEventRepository` today. Mitigation: keep the recorder writing through the CP
  `IEventRepository` with the resolved `TenantId` tag, so a future per-tenant fan-out only touches
  routing, not the emission call sites.
- **Migration discipline.** `billing_webhook_events` is additive; still run
  `has-pending-model-changes` (expect none) and declare config only in `TammaModelConfiguration`
  (single source).
- **Credential leakage into the event/audit store.** Mitigation: never persist the raw body beyond
  the `payload` column (Stripe's own redaction applies), never log the signing secret /
  `Stripe-Signature`; scrub `LastError` via `CredentialRedactor.Clean`.

---

## Acceptance criteria (mirrors the story)

- [ ] `POST /api/v1/billing/stripe/webhook` captures the raw body via `EnableBuffering()` and
      verifies with `Stripe.EventUtility.ConstructEvent`; the signing secret resolves through 35-1's
      cabinet path (`SecretScope.Platform`/`SecretPurpose.ApiKey`), never `IConfiguration`.
- [ ] Invalid/missing signature → `400` (no row, no event, WARN without raw body); unresolvable
      secret → `503` (never fail open).
- [ ] `BillingWebhookEvent` table dedupes on `UNIQUE(stripe_event_id)`; duplicate delivery → `200`
      with no reprocessing (one row, one projection, one DCB event).
- [ ] Tenant resolved via `BillingCustomer.StripeCustomerId → TenantId`; unresolved → `200 skipped`.
- [ ] Default handlers cover `customer.subscription.created/updated/deleted`,
      `invoice.created/finalized/paid/payment_failed`,
      `payment_intent.succeeded/payment_intent.payment_failed`,
      `customer.subscription.trial_will_end`, `charge.dispute.created`.
- [ ] Each dispatched event emits a `BILLING.*` DCB event via `IEventRepository.AppendAsync` with
      tags `{ tenantId, stripeEventId, eventType, stripeObjectId }`.
- [ ] Mirror updates are performed by sibling-registered `IBillingEventHandler`s; 35-5 ships only
      the dispatch seam + DCB emission + `NullBillingEventHandler` (no `BillingSubscription`/
      `BillingInvoice`/`BillingPaymentMethod` entities created here).
- [ ] Fast-ack: heavy work → `PlatformQueuedTask` (`Type = "billing.webhook.followup"`), never
      inline; p95 ack `< 2s`.
- [ ] Unknown event types → INFO + `200 skipped`; no `4xx`/`5xx` that triggers Stripe retries
      (except signature/secret).
- [ ] Admin `GET /api/v1/admin/billing/webhook-events` + `POST .../{id}/replay` (`PlatformOwnerAccess`)
      list + re-dispatch; replay of a `projected` event is a no-op.
- [ ] Single-user mode: webhook + admin routes unmapped (`NullBillingProvider`); SaaS mode: mapped,
      tenant resolution mandatory.
- [ ] Tenant isolation: a webhook for tenant A never writes a DCB event/row tagged tenant B;
      verified by an integration test with interleaved A/B deliveries.
- [ ] Unit + integration suite green (`sg docker -c "dotnet test ..."`); migration applies +
      rolls back cleanly; `has-pending-model-changes` reports none.
