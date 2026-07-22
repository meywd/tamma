using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Documents;
using Tamma.Core.Documents;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Documents;

/// <summary>
/// Story 39-8 (AC1/AC3, D9) — the escalation DISPOSITION surface. Because the
/// lifecycle has already EXITED when an escalation exists, dispositioning is not a
/// workflow resume: this service appends the <c>ESCALATION.RESOLVED</c> DCB event
/// directly, pairing on the <c>escalationId</c> tag of the originating
/// <c>ESCALATION.TRIGGERED</c> to compute the denormalized <c>durationMs</c> and
/// refusing a duplicate disposition.
///
/// <para><b>FAIL-LOUD, deliberately UNLIKE <see cref="Tamma.Api.Services.PromptStore.PromptEventsService"/>.</b>
/// There the event is a best-effort audit side-car of a prompt mutation, so a store
/// failure is swallowed. HERE the event IS the operation — an append that silently
/// dropped would leave the escalation un-dispositioned with the caller believing it
/// was resolved — so append failures propagate.</para>
///
/// <para><b>Disposition race (accepted).</b> <c>AppendAsync</c> has no unique
/// constraint on <c>escalationId</c>; the 409 check is read-then-write, so two
/// concurrent resolvers could both append. A rare double-RESOLVED is visible and
/// reconcilable on the stream (last-write-wins for dashboards) — noted here rather
/// than adding a table this story does not need.</para>
/// </summary>
public sealed class EscalationDispositionService
{
    // Bound on the pairing scan — an escalation is dispositioned close to when it triggers, so
    // the most-recent window comfortably covers a live escalation without an unbounded read.
    private const int PairingScanLimit = 500;

    private readonly IEventRepository _events;
    private readonly ILogger<EscalationDispositionService>? _logger;

    public EscalationDispositionService(IEventRepository events, ILogger<EscalationDispositionService>? logger = null)
    {
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Disposition the escalation identified by <paramref name="escalationId"/>. Returns
    /// <see cref="EscalationDispositionOutcome.NotFound"/> when no paired
    /// <c>ESCALATION.TRIGGERED</c> exists (→ 404), <see cref="EscalationDispositionOutcome.AlreadyResolved"/>
    /// when the escalation was already dispositioned (→ 409), or
    /// <see cref="EscalationDispositionOutcome.Resolved"/> after appending the
    /// <c>ESCALATION.RESOLVED</c> event (→ 200).
    /// </summary>
    public async Task<EscalationDispositionResult> DispositionAsync(
        Guid? tenantId,
        string escalationId,
        EscalationDisposition disposition,
        string? note,
        string deciderId,
        ApprovalChannel channel)
    {
        if (string.IsNullOrWhiteSpace(escalationId))
            return new EscalationDispositionResult(EscalationDispositionOutcome.NotFound, 0);

        // Pair on the ESCALATION.TRIGGERED carrying this escalationId (D9).
        var triggered = await _events.QueryAsync(tenantId, ApprovalEvents.EscalationTriggered, null, PairingScanLimit);
        var trigger = triggered.FirstOrDefault(e => TagValue(e, "escalationId") == escalationId);
        if (trigger is null)
        {
            _logger?.LogWarning("No ESCALATION.TRIGGERED found for escalationId {EscalationId}", escalationId);
            return new EscalationDispositionResult(EscalationDispositionOutcome.NotFound, 0);
        }

        // Refuse a duplicate disposition (409).
        var resolved = await _events.QueryAsync(tenantId, ApprovalEvents.EscalationResolved, null, PairingScanLimit);
        if (resolved.Any(e => TagValue(e, "escalationId") == escalationId))
        {
            _logger?.LogWarning("Escalation {EscalationId} already dispositioned", escalationId);
            return new EscalationDispositionResult(EscalationDispositionOutcome.AlreadyResolved, 0);
        }

        var durationMs = (long)(DateTime.UtcNow - DateTime.SpecifyKind(trigger.CreatedAt, DateTimeKind.Utc)).TotalMilliseconds;
        if (durationMs < 0) durationMs = 0;

        // Copy the trigger's queryable tags so the RESOLVED row pairs on the stream, and add the
        // escalationId (always) even if the trigger had it only structurally.
        var tags = CopyTags(trigger, "issueId", "documentId", "documentType", "correlationId");
        tags["escalationId"] = escalationId;
        if (tenantId is not null) tags["tenantId"] = tenantId.Value.ToString("D");

        var data = new Dictionary<string, object?>
        {
            ["disposition"] = disposition.ToWire(),
            ["resolvedBy"] = deciderId,
            ["channel"] = channel.ToWire(),
            ["durationMs"] = durationMs,
        };
        if (!string.IsNullOrWhiteSpace(note)) data["note"] = note;

        var metadata = new Dictionary<string, object?>
        {
            ["workflowVersion"] = "1.0.0",
            ["eventSource"] = "system",
        };

        var evt = new DomainEvent
        {
            Type = ApprovalEvents.EscalationResolved,
            TenantId = tenantId,
            IssueNumber = trigger.IssueNumber,
            Tags = JsonSerializer.Serialize(tags),
            Metadata = JsonSerializer.Serialize(metadata),
            Data = JsonSerializer.Serialize(data),
        };

        // FAIL-LOUD — the event IS the operation; an append failure must propagate.
        await _events.AppendAsync(evt);

        _logger?.LogInformation(
            "Dispositioned escalation {EscalationId} as {Disposition} (durationMs={Duration})",
            escalationId, disposition.ToWire(), durationMs);

        return new EscalationDispositionResult(EscalationDispositionOutcome.Resolved, durationMs);
    }

    private static string? TagValue(DomainEvent evt, string key)
    {
        if (string.IsNullOrWhiteSpace(evt.Tags)) return null;
        try
        {
            using var doc = JsonDocument.Parse(evt.Tags);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(key, out var v)
                ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> CopyTags(DomainEvent evt, params string[] keys)
    {
        var tags = new Dictionary<string, object?>();
        foreach (var key in keys)
        {
            var value = TagValue(evt, key);
            if (!string.IsNullOrWhiteSpace(value)) tags[key] = value;
        }
        return tags;
    }
}

/// <summary>Terminal outcome of a disposition attempt (maps to 200/404/409).</summary>
public enum EscalationDispositionOutcome
{
    Resolved,
    NotFound,
    AlreadyResolved,
}

/// <summary>Result of <see cref="EscalationDispositionService.DispositionAsync"/>.</summary>
public sealed record EscalationDispositionResult(EscalationDispositionOutcome Outcome, long DurationMs);
