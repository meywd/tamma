using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tamma.Core.Redaction;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Billing;

/// <summary>
/// Story 35-5 — the webhook ingestion core. Flow (fresh delivery):
/// <list type="number">
///   <item>Insert <c>BillingWebhookEvent{received}</c>; a <c>UNIQUE(StripeEventId)</c>
///     collision → <c>Duplicate</c> (idempotent ack — AC5).</item>
///   <item>Resolve tenant via <c>BillingCustomer.StripeCustomerId</c>; no match →
///     <c>skipped</c> (AC6).</item>
///   <item>Resolve handler (or <see cref="NullBillingEventHandler"/> for unknown
///     types → <c>skipped</c>, AC11); run it (mirror write in sibling handlers +
///     <c>BILLING.*</c> DCB emit, AC8).</item>
///   <item>Any returned <see cref="BillingFollowup"/> → enqueue a
///     <c>billing.webhook.followup</c> <c>PlatformQueuedTask</c> (fast-ack, AC10);
///     stamp <c>enqueued</c>, else <c>projected</c>.</item>
///   <item>Handler exception → <c>failed</c> + scrubbed <c>LastError</c>, still
///     ack (recovery via admin replay, not Stripe retries).</item>
/// </list>
///
/// <para><b>Idempotency / atomicity.</b> The dedup row is inserted FIRST, so a
/// concurrent redelivery collides on the unique index and never re-projects.
/// The <c>BILLING.*</c> DCB event routes through <see cref="IEventRepository"/>
/// (a tenant-routed store), so a single cross-DB transaction is not possible;
/// the guarantee instead is "insert-first dedup + admin replay" — a crash after
/// insert leaves a non-<c>projected</c> row that replay re-dispatches, and a
/// redelivery is deduped. This is the <c>PlatformWebhookDelivery</c> precedent.</para>
/// </summary>
public sealed class StripeWebhookProcessor : IStripeWebhookProcessor
{
    private readonly ControlPlaneDbContext _db;
    private readonly IEventRepository _events;
    private readonly IPlatformQueuedTaskRepository _tasks;
    private readonly IBillingEventHandlerRegistry _registry;
    private readonly NullBillingEventHandler _nullHandler;
    private readonly TimeProvider _clock;
    private readonly ILogger<StripeWebhookProcessor> _logger;

    public StripeWebhookProcessor(
        ControlPlaneDbContext db,
        IEventRepository events,
        IPlatformQueuedTaskRepository tasks,
        IBillingEventHandlerRegistry registry,
        NullBillingEventHandler nullHandler,
        TimeProvider clock,
        ILogger<StripeWebhookProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(nullHandler);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _events = events;
        _tasks = tasks;
        _registry = registry;
        _nullHandler = nullHandler;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<WebhookProcessResult> ProcessAsync(
        Stripe.Event stripeEvent, string rawPayload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stripeEvent);
        ArgumentNullException.ThrowIfNull(rawPayload);

        var stripeEventId = stripeEvent.Id ?? string.Empty;
        var eventType = stripeEvent.Type ?? string.Empty;
        var (objectId, customerId) = ExtractIds(rawPayload);
        var now = _clock.GetUtcNow().UtcDateTime;

        _logger.LogInformation(
            "Stripe webhook received: {EventType} (stripeEventId={StripeEventId}, "
            + "stripeObjectId={StripeObjectId}).",
            eventType, stripeEventId, objectId);

        // ── 1. Dedup. Pre-check is a cheap fast-path (and the only guard EF
        // InMemory can enforce in unit tests); the unique-index catch below is
        // the authoritative, race-safe guard under concurrent Stripe retries. ──
        var already = await _db.BillingWebhookEvents
            .AsNoTracking()
            .AnyAsync(e => e.StripeEventId == stripeEventId, ct)
            .ConfigureAwait(false);
        if (already)
        {
            _logger.LogDebug(
                "Duplicate Stripe delivery {StripeEventId}; acking without reprocessing.",
                stripeEventId);
            return WebhookProcessResult.Duplicate;
        }

        var row = new BillingWebhookEvent
        {
            Id = Guid.NewGuid(),
            StripeEventId = stripeEventId,
            EventType = eventType,
            StripeObjectId = objectId,
            Status = "received",
            Attempts = 1,
            Payload = string.IsNullOrWhiteSpace(rawPayload) ? "{}" : rawPayload,
            ReceivedAt = now,
        };
        _db.BillingWebhookEvents.Add(row);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: a concurrent delivery of the same event won the UNIQUE index
            // (SqlState 23505). ONLY that is a duplicate; any other DbUpdateException
            // (deadlock, timeout, connection reset, NOT-NULL/FK violation, …) is a
            // transient/real write failure that MUST bubble so the endpoint returns
            // a non-2xx and Stripe retries — swallowing it as "duplicate" would lose
            // the billing event forever (IStripeWebhookProcessor contract).
            _db.Entry(row).State = EntityState.Detached;
            _logger.LogDebug(
                "Duplicate Stripe delivery {StripeEventId} (unique-index race); acking.",
                stripeEventId);
            return WebhookProcessResult.Duplicate;
        }

        return await DispatchAsync(row, stripeEvent, customerId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<WebhookProcessResult?> ReplayAsync(
        Guid webhookEventId, CancellationToken ct = default)
    {
        var row = await _db.BillingWebhookEvents
            .FirstOrDefaultAsync(e => e.Id == webhookEventId, ct)
            .ConfigureAwait(false);
        if (row is null) return null;

        // Idempotent: a terminal projected/enqueued row is a no-op on replay (AC12).
        if (row.Status is "projected" or "enqueued")
        {
            _logger.LogInformation(
                "Replay of {StripeEventId} is a no-op — already {Status}.",
                row.StripeEventId, row.Status);
            return new WebhookProcessResult(row.Status);
        }

        Stripe.Event stripeEvent;
        try
        {
            stripeEvent = new StripeEventVerifier().Parse(row.Payload);
        }
        catch (Exception ex)
        {
            row.Attempts += 1;
            row.Status = "failed";
            row.LastError = CredentialRedactor.Clean(ex.Message);
            row.ProcessedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogError(ex,
                "Replay of {StripeEventId} failed to parse stored payload.", row.StripeEventId);
            return WebhookProcessResult.Failed;
        }

        var (_, customerId) = ExtractIds(row.Payload);
        row.Attempts += 1;
        _logger.LogInformation(
            "Admin replay of Stripe webhook {StripeEventId} (attempt {Attempts}).",
            row.StripeEventId, row.Attempts);
        return await DispatchAsync(row, stripeEvent, customerId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared dispatch (tenant-resolve → handler → DCB emit → follow-up enqueue →
    /// status stamp). Operates on an already-persisted <paramref name="row"/>.
    /// </summary>
    private async Task<WebhookProcessResult> DispatchAsync(
        BillingWebhookEvent row, Stripe.Event stripeEvent, string? customerId, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // ── 2. Tenant resolution (AC6). ──
        var tenantId = await ResolveTenantAsync(customerId, ct).ConfigureAwait(false);
        if (tenantId is null)
        {
            // A verified event we cannot tenant-resolve must NEVER disappear
            // without a trace. In particular a real charge.dispute.created has NO
            // top-level `customer` on its Dispute object, so it always lands here —
            // it must be audited AND followed up, not silently skipped.
            return await HandleUnresolvedTenantAsync(row, now, ct).ConfigureAwait(false);
        }

        row.TenantId = tenantId;
        var ctx = new BillingWebhookContext(
            stripeEvent, tenantId.Value, row.EventType, row.StripeEventId, row.StripeObjectId, row.Payload);

        // ── 3. Handler resolution. Unknown type → NullBillingEventHandler → skipped (AC11). ──
        var handler = _registry.Resolve(row.EventType);
        if (handler is null)
        {
            await _nullHandler.HandleAsync(ctx, ct).ConfigureAwait(false);
            row.Status = "skipped";
            row.ProcessedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return WebhookProcessResult.Skipped;
        }

        row.Status = "processing";

        // ── 4. Project + emit + follow-up enqueue + fast-ack, as ONE fallible unit. ──
        // The follow-up enqueue lives INSIDE the try so an enqueue/commit failure
        // stamps the row `failed` (deliberately re-drivable via admin replay)
        // instead of leaving a non-terminal `received`/`processing` row that a
        // Stripe redelivery would dedup away — which would drop the follow-up while
        // the projection had already emitted.
        BillingFollowup? followup;
        try
        {
            followup = await handler.HandleAsync(ctx, ct).ConfigureAwait(false);

            if (followup is not null)
            {
                await EnqueueFollowupAsync(followup.Payload, tenantId, now, ct).ConfigureAwait(false);
                row.Status = "enqueued";
            }
            else
            {
                row.Status = "projected";
            }
            row.ProcessedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.LastError = CredentialRedactor.Clean(ex.Message);
            row.ProcessedAt = _clock.GetUtcNow().UtcDateTime;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Audit the failure (tenant-scoped) but STILL ack 200 — recovery is
            // our own admin replay + follow-up queue, not Stripe retries.
            await SafeEmitAsync(BillingWebhookDcbEvents.Operational(
                BillingWebhookEventTypes.DcbWebhookFailed, tenantId, row.StripeEventId,
                row.EventType, row.StripeObjectId, "handler_exception")).ConfigureAwait(false);
            _logger.LogError(ex,
                "Billing handler {Handler} threw for {StripeEventId} ({EventType}); "
                + "recorded failed, acked.",
                handler.GetType().Name, row.StripeEventId, row.EventType);
            return WebhookProcessResult.Failed;
        }

        if (followup is not null)
        {
            _logger.LogDebug(
                "Enqueued billing.webhook.followup ({Subtype}) for {StripeEventId}.",
                followup.Subtype, row.StripeEventId);
            return WebhookProcessResult.Enqueued;
        }

        _logger.LogInformation(
            "Projected Stripe webhook {StripeEventId} ({EventType}) for tenant {TenantId}.",
            row.StripeEventId, row.EventType, tenantId);
        return WebhookProcessResult.Projected;
    }

    /// <summary>
    /// A verified event that could not be tenant-resolved. Two cases:
    /// <list type="bullet">
    ///   <item><b>Known-relevant type</b> (a handler claims this Stripe type) but no
    ///     tenant — e.g. a real <c>charge.dispute.created</c> whose Dispute object
    ///     carries NO top-level <c>customer</c>. Emit a platform-scope
    ///     <c>BILLING.WEBHOOK.SKIPPED</c> audit event so the event is never lost,
    ///     and for a dispute ALSO enqueue a <c>billing.webhook.followup</c>
    ///     (carrying the charge / payment_intent ids) so the chargeback is resolved
    ///     out-of-band (the follow-up handler can later map charge → customer →
    ///     tenant via a Stripe lookup, or an operator does). The invariant: a
    ///     verified dispute NEVER disappears without an audit trail + a follow-up.</item>
    ///   <item><b>Unknown type</b> (no handler claims it) with no tenant — a benign
    ///     irrelevant delivery; recorded <c>skipped</c> with no audit-event spam.</item>
    /// </list>
    /// </summary>
    private async Task<WebhookProcessResult> HandleUnresolvedTenantAsync(
        BillingWebhookEvent row, DateTime now, CancellationToken ct)
    {
        row.TenantId = null;
        row.ProcessedAt = now;

        var isKnownRelevant = _registry.Resolve(row.EventType) is not null;
        if (!isKnownRelevant)
        {
            // Unknown/irrelevant Stripe type + no tenant: nothing to project and
            // nothing worth auditing on the DCB stream (avoid SKIPPED spam).
            row.Status = "skipped";
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogWarning(
                "Stripe webhook {StripeEventId} ({EventType}) has no tenant and no "
                + "handler; recording skipped.",
                row.StripeEventId, row.EventType);
            return WebhookProcessResult.Skipped;
        }

        // Platform-scope audit: a KNOWN-relevant event we could not tenant-resolve.
        await SafeEmitAsync(BillingWebhookDcbEvents.Operational(
            BillingWebhookEventTypes.DcbWebhookSkipped, tenantId: null, row.StripeEventId,
            row.EventType, row.StripeObjectId, "tenant_unresolved")).ConfigureAwait(false);

        // A dispute MUST also get a follow-up so the chargeback is not dropped.
        if (row.EventType == BillingWebhookEventTypes.ChargeDisputeCreated)
        {
            var (charge, paymentIntent) = ExtractDisputeRefs(row.Payload);
            var followupPayload = JsonSerializer.Serialize(new
            {
                reason = "dispute_unresolved_tenant",
                stripeEventId = row.StripeEventId,
                disputeId = row.StripeObjectId,
                charge,
                paymentIntent,
            });
            try
            {
                await EnqueueFollowupAsync(followupPayload, tenantId: null, now, ct).ConfigureAwait(false);
                row.Status = "enqueued";
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                row.Status = "failed";
                row.LastError = CredentialRedactor.Clean(ex.Message);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _logger.LogError(ex,
                    "Dispute {StripeEventId} follow-up enqueue failed after SKIPPED audit; "
                    + "recorded failed (re-drivable via replay).",
                    row.StripeEventId);
                return WebhookProcessResult.Failed;
            }
            _logger.LogWarning(
                "Dispute {StripeEventId} (charge={Charge}, paymentIntent={PaymentIntent}) could "
                + "not be tenant-resolved; emitted BILLING.WEBHOOK.SKIPPED + enqueued follow-up "
                + "for out-of-band resolution.",
                row.StripeEventId, charge, paymentIntent);
            return WebhookProcessResult.Enqueued;
        }

        // Other known-relevant types with no tenant: audited, then skipped.
        row.Status = "skipped";
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Stripe webhook {StripeEventId} ({EventType}) maps to no BillingCustomer; "
            + "emitted BILLING.WEBHOOK.SKIPPED (platform audit), recording skipped.",
            row.StripeEventId, row.EventType);
        return WebhookProcessResult.Skipped;
    }

    private async Task EnqueueFollowupAsync(
        string payload, Guid? tenantId, DateTime now, CancellationToken ct) =>
        await _tasks.EnqueueAsync(new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = BillingWebhookEventTypes.FollowupTaskType,
            TenantId = tenantId,
            Payload = payload,
            Status = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        }, ct).ConfigureAwait(false);

    private async Task<Guid?> ResolveTenantAsync(string? customerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerId)) return null;
        return await _db.BillingCustomers
            .AsNoTracking()
            .Where(c => c.StripeCustomerId == customerId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A <see cref="DbUpdateException"/> is a unique-violation (23505) only — the
    /// idempotent-dedup case. Mirrors <c>EventRepository.AppendAsync</c>. Any other
    /// DbUpdateException is a transient/real failure that must not be masked as a
    /// duplicate ack.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg &&
        pg.SqlState == PostgresErrorCodes.UniqueViolation;

    private async Task SafeEmitAsync(DomainEvent evt)
    {
        try
        {
            await _events.AppendAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed audit-event append must never mask the original outcome.
            _logger.LogWarning(ex,
                "Failed to append operational billing event {Type}.", evt.Type);
        }
    }

    /// <summary>
    /// Extract <c>data.object.id</c> (the primary Stripe object id) and
    /// <c>data.object.customer</c> (a <c>cus_...</c> string, or the id of an
    /// expanded customer object) from the raw event JSON. Deterministic and
    /// SDK-shape-independent — never re-parses through the typed model. Returns
    /// <c>(null, null)</c> when the JSON is malformed or lacks the fields.
    /// </summary>
    internal static (string? ObjectId, string? CustomerId) ExtractIds(string rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("object", out var obj)
                || obj.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? objectId = obj.TryGetProperty("id", out var idEl)
                && idEl.ValueKind == JsonValueKind.String
                ? idEl.GetString()
                : null;

            return (objectId, ReadIdOrString(obj, "customer"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Extract a Dispute object's <c>charge</c> and <c>payment_intent</c> reference
    /// ids from the raw event JSON. A real Stripe Dispute has NO top-level
    /// <c>customer</c>, so these are the only handles back to the tenant — the
    /// dispute follow-up carries them for out-of-band resolution (e.g. a Stripe
    /// charge lookup). Each may be a bare id string or an expanded object with an
    /// <c>id</c>. Returns <c>(null, null)</c> for malformed JSON or missing fields.
    /// </summary>
    internal static (string? Charge, string? PaymentIntent) ExtractDisputeRefs(string rawPayload)
    {
        if (string.IsNullOrWhiteSpace(rawPayload)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(rawPayload);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("object", out var obj)
                || obj.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (ReadIdOrString(obj, "charge"), ReadIdOrString(obj, "payment_intent"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Read <paramref name="prop"/> from <paramref name="obj"/> as either a bare id
    /// string (<c>"cus_…"</c>/<c>"ch_…"</c>) or the <c>id</c> of an expanded object.
    /// Returns <c>null</c> when absent or of any other shape.
    /// </summary>
    private static string? ReadIdOrString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Object when el.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String => id.GetString(),
                _ => null,
            }
            : null;
}
