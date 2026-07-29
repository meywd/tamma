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

        if (type == AllowedType && decision.Source == ActionAssignmentSource.SystemDefault)
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
