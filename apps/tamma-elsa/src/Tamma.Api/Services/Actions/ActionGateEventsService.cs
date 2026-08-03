using System.Text.Json;
using Tamma.Core.Actions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-5 (AC13, D11) — the ONE audit event family for the autonomy gate,
/// on the <c>AcceptanceRulesEventsService</c> template (const type strings,
/// tags, <c>{workflowVersion, eventSource}</c> metadata, swallowing try/catch)
/// with TWO deliberate deviations:
///
/// <list type="bullet">
/// <item><b>Denials under enforcement are NOT swallowed</b> — a block with no
/// audit row is a compliance hole, so <c>.DENIED</c> / <c>.REQUIRES_HUMAN</c>
/// emission failures rethrow when the decision was enforced.</item>
/// <item><b><c>.ALLOWED</c> is volume-gated</b>: emitted only when the
/// resolution's provenance is NOT <c>system-default</c> — otherwise a 40-call
/// tool loop writes 40 rows saying "nothing happened". (The plan's "or
/// Enforced" arm is dropped: under epic D1 enforce DEFAULTS to true, so that
/// arm would have defeated the volume gate entirely.)</item>
/// </list>
///
/// <para>The C# type is named <c>ActionGateEventsService</c> — the one
/// deliberate exception to the <c>AutonomyGate*</c> naming rule — because the
/// <c>ACTION.GATE.*</c> strings are wire values consumed by dashboards, and
/// <c>AUTONOMY.GATE.*</c> would be a second name for the same thing. Appends
/// go DIRECTLY through <see cref="IEventRepository"/>: <c>TammaEventEmitter</c>
/// structurally requires an <c>ActivityExecutionContext</c> and the tool loop
/// runs inside a blocking HTTP request.</para>
/// </summary>
public sealed class ActionGateEventsService
{
    public const string AllowedType = "ACTION.GATE.ALLOWED";
    public const string RequiresHumanType = "ACTION.GATE.REQUIRES_HUMAN";
    public const string DeniedType = "ACTION.GATE.DENIED";
    public const string AuthorizedType = "ACTION.GATE.AUTHORIZED";
    public const string AuthorizationDeniedType = "ACTION.GATE.AUTHORIZATION_DENIED";
    public const string PrincipalUnresolvedType = "ACTION.GATE.PRINCIPAL_UNRESOLVED";
    public const string EvaluationFailedType = "ACTION.GATE.EVALUATION_FAILED";
    public const string AssignmentChangedType = "ACTION.GATE.ASSIGNMENT_CHANGED";

    /// <summary>
    /// F11 — ONE row per decision that the break-glass override let through a
    /// degraded read instead of failing closed. Distinct type, not a tag on
    /// <see cref="AllowedType"/>: "an operator suspended the fail-closed posture
    /// and this specific action proceeded because of it" is the fact a reviewer
    /// after the outage needs to select on, and it must not be lost inside the
    /// allow stream or filtered out by the volume gate.
    /// </summary>
    public const string BreakGlassBypassType = "ACTION.GATE.BREAK_GLASS_BYPASS";

    private readonly IEventRepository _events;
    private readonly ILogger<ActionGateEventsService>? _logger;

    public ActionGateEventsService(
        IEventRepository events, ILogger<ActionGateEventsService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Emit the decision event for one gate evaluation. Suppresses
    /// <c>.ALLOWED</c> for pure shipped-default resolutions (volume gate);
    /// rethrows an append failure ONLY for an enforced denial/escalation.
    /// </summary>
    public async Task EmitDecisionAsync(
        AutonomyDecision decision, AutonomyQuery query, string? issueId = null)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(query);

        var type = decision.Outcome switch
        {
            AutonomyOutcome.Automated => AllowedType,
            AutonomyOutcome.RequiresHuman => RequiresHumanType,
            AutonomyOutcome.Denied => DeniedType,
            _ => AllowedType,
        };

        if (type == AllowedType
            && decision.Source == ActionAssignmentSource.SystemDefault
            // Story 43-13 (D9) — ONE carve-out: an allow whose reason is "the
            // caller is a person" is precisely the caller-kind predicate's work
            // product and must reach the audit stream even at SystemDefault
            // source ("passed because human" must be distinguishable from
            // "automated at level"). Volume risk is bounded: human traffic on
            // enforced routes is zero today (all 16 are EngineServiceOnly).
            // Machinery short-circuit allows deliberately keep SystemDefault
            // source and STAY suppressed — Seam D would otherwise emit one row
            // per actor per tick.
            && decision.Reason != AutonomyGateEvaluator.ReasonCallerHuman)
        {
            return; // volume gate — "nothing happened" rows are noise
        }

        // The 13-tag set (AC13).
        var tags = new Dictionary<string, string?>
        {
            ["actionKey"] = decision.Action.ToWire(),
            ["actionGroup"] = decision.Group.ToWire(),
            ["risk"] = decision.Risk.ToWire(),
            ["autonomyLevel"] = decision.AutonomyLevel.ToString(),
            ["effectiveMinAutonomy"] = decision.EffectiveMinAutonomy.ToString(),
            ["assignmentSource"] = SourceWire(decision.Source),
            ["outcome"] = decision.Outcome.ToString().ToLowerInvariant(),
            ["enforced"] = decision.Enforced ? "true" : "false",
            // F6 — a queryable "this decision was made over an unreadable policy
            // input" tag, so a degraded window is one tag filter away rather
            // than an inference from provenance. F11 — a break-glass bypass is
            // ALSO a decision made over an unreadable input (it can only occur
            // under degradation), so it sets the same tag; `assignmentSource`
            // and the dedicated BREAK_GLASS_BYPASS row are what tell the two
            // apart.
            ["degraded"] = decision.Source
                is ActionAssignmentSource.Unavailable or ActionAssignmentSource.BreakGlass
                ? "true" : "false",
            ["breakGlass"] =
                decision.Source == ActionAssignmentSource.BreakGlass ? "true" : "false",
            // Story 43-13 AC8 — WHO the decision was taken for, so the trail
            // distinguishes "passed because human" from "automated at level"
            // from "machinery, not dial-governed".
            ["callerKind"] = query.Caller.ToWire(),
        };
        if (query.Role is not null) tags["role"] = query.Role;
        if (query.CorrelationId is not null) tags["correlationId"] = query.CorrelationId;
        if (issueId is not null) tags["issueId"] = issueId;
        if (query.Principal.TenantId is Guid tid) tags["tenantId"] = tid.ToString();
        if (query.Principal.UserId is Guid uid) tags["userId"] = uid.ToString();

        var data = new Dictionary<string, object?>
        {
            ["reason"] = decision.Reason,
            ["operation"] = query.Operation,
            ["target"] = query.Target,
            ["enabled"] = decision.Enabled,
            ["allowedRoles"] = decision.AllowedRoles,
        };

        var mustNotSwallow =
            decision.Enforced && type is DeniedType or RequiresHumanType;
        await AppendAsync(type, query.Principal.TenantId, tags, data, mustNotSwallow)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// F11 — record ONE bypassed decision. <b>Deliberately on the NON-SWALLOWING
    /// append path</b>, unlike every other allow-shaped emission here.
    ///
    /// <para>The F6 close reasoned that rethrowing on a failed audit append for an
    /// ALLOW would turn an event-store blip into a second outage on a surface that
    /// is deliberately staying open, and that reasoning still governs
    /// <c>.ALLOWED</c>. Break-glass is the one exception, because the audit row is
    /// not commentary on the decision — it IS the justification for having a
    /// bypass at all. An unrecorded bypass is indistinguishable from an
    /// unauthorised one, and "loud and audited" is the entire condition on which
    /// this lever exists. So a bypass that cannot be recorded does not happen
    /// quietly; the append failure propagates.</para>
    /// </summary>
    /// <param name="seam">Which enforcement surface bypassed (<c>tool-loop</c>,
    /// <c>autonomy-gate</c>) — a bypass at Seam B and one at a 43-9 seam have very
    /// different blast radii and a reviewer must be able to tell them apart.</param>
    public Task EmitBreakGlassBypassAsync(
        string actionKeyWire,
        string? actionGroupWire,
        BreakGlassState breakGlass,
        string seam,
        string outcome,
        int autonomyLevel,
        int? effectiveMinAutonomy,
        Guid? tenantId = null,
        Guid? userId = null,
        string? correlationId = null,
        string? degradedReason = null)
    {
        ArgumentNullException.ThrowIfNull(breakGlass);

        var tags = new Dictionary<string, string?>
        {
            ["actionKey"] = actionKeyWire,
            ["outcome"] = outcome,
            ["seam"] = seam,
            ["breakGlass"] = "true",
            ["degraded"] = "true",
            ["assignmentSource"] = SourceWire(ActionAssignmentSource.BreakGlass),
            ["autonomyLevel"] = autonomyLevel.ToString(),
            ["expiresAtUtc"] = breakGlass.ExpiresAtUtc?.ToString("O"),
        };
        if (actionGroupWire is not null) tags["actionGroup"] = actionGroupWire;
        if (effectiveMinAutonomy is int m) tags["effectiveMinAutonomy"] = m.ToString();
        if (correlationId is not null) tags["correlationId"] = correlationId;
        if (tenantId is Guid t) tags["tenantId"] = t.ToString();
        if (userId is Guid u) tags["userId"] = u.ToString();

        return AppendAsync(
            BreakGlassBypassType, tenantId, tags,
            new Dictionary<string, object?>
            {
                ["reason"] = breakGlass.ReasonOrUnspecified,
                ["expiresAtUtc"] = breakGlass.ExpiresAtUtc?.ToString("O"),
                ["degradedReason"] = degradedReason,
            },
            mustNotSwallow: true);
    }

    /// <summary>SaaS request with no resolvable tenant — resolved against the
    /// platform scope only (AC7).</summary>
    public Task EmitPrincipalUnresolvedAsync(string? detail = null)
        => AppendAsync(PrincipalUnresolvedType, tenantId: null,
            new Dictionary<string, string?>(),
            new Dictionary<string, object?> { ["detail"] = detail },
            mustNotSwallow: false);

    /// <summary>An evaluation blew up before producing a decision.</summary>
    public Task EmitEvaluationFailedAsync(string actionKeyWire, string error, Guid? tenantId, Guid? userId)
    {
        var tags = new Dictionary<string, string?> { ["actionKey"] = actionKeyWire };
        if (tenantId is Guid t) tags["tenantId"] = t.ToString();
        if (userId is Guid u) tags["userId"] = u.ToString();
        return AppendAsync(EvaluationFailedType, tenantId, tags,
            new Dictionary<string, object?> { ["error"] = error },
            mustNotSwallow: false);
    }

    /// <summary>An admin changed an assignment (43-6's write path; the change
    /// itself is the durable fact — this event is best-effort).</summary>
    public Task EmitAssignmentChangedAsync(
        Guid? tenantId, Guid? userId, Guid? actorUserId,
        string scope, string targetKind, string targetKey,
        string field, object? oldValue, object? newValue)
    {
        var tags = new Dictionary<string, string?>
        {
            ["targetKind"] = targetKind,
            ["targetKey"] = targetKey,
            ["scope"] = scope,
            ["field"] = field,
        };
        if (tenantId is Guid t) tags["tenantId"] = t.ToString();
        if (userId is Guid u) tags["userId"] = u.ToString();
        if (actorUserId is Guid a) tags["actorUserId"] = a.ToString();
        return AppendAsync(AssignmentChangedType, tenantId, tags,
            new Dictionary<string, object?>
            {
                ["field"] = field,
                ["oldValue"] = oldValue,
                ["newValue"] = newValue,
            },
            mustNotSwallow: false);
    }

    /// <summary>A ledger grant was consumed (43-9 uses this at the seams).</summary>
    public Task EmitAuthorizedAsync(
        Guid? tenantId, Guid? userId, string actionKeyWire, string correlationId, Guid authorizationId)
    {
        var tags = new Dictionary<string, string?>
        {
            ["actionKey"] = actionKeyWire,
            ["correlationId"] = correlationId,
            ["authorizationId"] = authorizationId.ToString(),
        };
        if (tenantId is Guid t) tags["tenantId"] = t.ToString();
        if (userId is Guid u) tags["userId"] = u.ToString();
        return AppendAsync(AuthorizedType, tenantId, tags,
            new Dictionary<string, object?>(), mustNotSwallow: false);
    }

    /// <summary>A pending authorization was denied by a person.</summary>
    public Task EmitAuthorizationDeniedAsync(
        Guid? tenantId, Guid? userId, string targetKey, string correlationId, Guid authorizationId)
    {
        var tags = new Dictionary<string, string?>
        {
            ["targetKey"] = targetKey,
            ["correlationId"] = correlationId,
            ["authorizationId"] = authorizationId.ToString(),
        };
        if (tenantId is Guid t) tags["tenantId"] = t.ToString();
        if (userId is Guid u) tags["userId"] = u.ToString();
        return AppendAsync(AuthorizationDeniedType, tenantId, tags,
            new Dictionary<string, object?>(), mustNotSwallow: false);
    }

    private static string SourceWire(ActionAssignmentSource source) => source switch
    {
        ActionAssignmentSource.PlatformCeiling => "platform-ceiling",
        ActionAssignmentSource.AlwaysEscalateLegacy => "always-escalate-legacy",
        ActionAssignmentSource.ActionOverride => "action-override",
        ActionAssignmentSource.GroupOverride => "group-override",
        // F6 — a fail-closed decision made over an UNREADABLE policy input. It
        // must never share a wire value with system-default: "we applied the
        // shipped default" and "we could not read policy and refused to
        // automate" are opposite facts about the same audit stream.
        ActionAssignmentSource.Unavailable => "policy-unavailable",
        // F11 — a THIRD value. It must not share a wire with `policy-unavailable`
        // (that one means the gate REFUSED) nor with `system-default` (that one
        // means nothing was wrong).
        ActionAssignmentSource.BreakGlass => "break-glass",
        _ => "system-default",
    };

    private async Task AppendAsync(
        string type,
        Guid? tenantId,
        IReadOnlyDictionary<string, string?> tags,
        IReadOnlyDictionary<string, object?> data,
        bool mustNotSwallow)
    {
        try
        {
            var metadata = new Dictionary<string, object?>
            {
                ["workflowVersion"] = "1.0.0",
                ["eventSource"] = "system",
            };
            var evt = new DomainEvent
            {
                Type = type,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(tags),
                Metadata = JsonSerializer.Serialize(metadata),
                Data = JsonSerializer.Serialize(data),
            };
            await _events.AppendAsync(evt).ConfigureAwait(false);
        }
        catch (Exception ex) when (!mustNotSwallow)
        {
            _logger?.LogWarning(ex, "Failed to emit action-gate event {Type}", type);
        }
        // An enforced denial with no audit row is a compliance hole (AC13):
        // the exception propagates to the caller instead of being swallowed.
    }
}
