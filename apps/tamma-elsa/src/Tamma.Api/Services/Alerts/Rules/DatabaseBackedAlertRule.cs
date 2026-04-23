using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — generic <see cref="IAlertRule"/> impl backed
/// by an <c>alert_rules</c> row. Parses the predicate DSL once at
/// construction (on the registry-load path) and memoises the AST, so
/// per-event evaluation is pure CPU + window-store lookups.
///
/// <para>Emitted <see cref="AlertPayload"/> carries the rule's
/// severity + name/description with mustache-lite substitution of
/// <c>{tenantId}</c> / <c>{correlationId}</c> / <c>{eventType}</c>
/// from the triggering event. No per-rule custom templates — the
/// payload shape is fixed per the Wave C.2 brief.</para>
/// </summary>
public sealed class DatabaseBackedAlertRule : IAlertRule
{
    private readonly AlertRule _row;
    private readonly AlertRulePredicate _predicate;

    public DatabaseBackedAlertRule(AlertRule row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _row = row;
        _predicate = AlertRulePredicateParser.Parse(row.Predicate);
    }

    public Guid Id => _row.Id;
    public string EventType => _row.EventType;
    public int ThrottleSeconds => _row.ThrottleSeconds;

    /// <summary>Row-level severity constant.</summary>
    public string Severity => _row.Severity;

    /// <summary>Row-level name constant.</summary>
    public string Name => _row.Name;

    /// <summary>Row-level description constant.</summary>
    public string Description => _row.Description;

    /// <summary>Target channels linked to this rule.</summary>
    public IReadOnlyList<Guid> ChannelIds => _row.ChannelIds;

    public AlertPayload? Evaluate(AlertRuleContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!_predicate.Evaluate(ctx)) return null;
        return BuildPayload(ctx);
    }

    private AlertPayload BuildPayload(AlertRuleContext ctx)
    {
        var evt = ctx.Event;
        var tenantTag = ctx.TryGetTag("tenantId", out var t) ? t : null;
        var correlationId = ctx.TryGetTag("correlationId", out var c) ? c : null;

        // mustache-lite interpolation on title / description.
        var title = Interpolate(_row.Name, ctx, evt);
        var description = Interpolate(_row.Description, ctx, evt);

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ruleId"] = _row.Id.ToString("N"),
            ["ruleName"] = _row.Name,
            ["eventType"] = evt.Type,
            ["eventId"] = evt.Id.ToString("N"),
        };

        return new AlertPayload(
            Severity: _row.Severity,
            Title: title,
            Description: description,
            CorrelationId: correlationId,
            TenantId: evt.TenantId,
            RuleId: _row.Id,
            Metadata: metadata);
    }

    private static string Interpolate(
        string template, AlertRuleContext ctx, DomainEvent evt)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{'))
            return template;

        var result = template;
        // Known static substitutions.
        result = result.Replace("{eventType}", evt.Type);
        result = result.Replace(
            "{tenantId}",
            evt.TenantId?.ToString("N") ?? "(platform)");
        if (ctx.TryGetTag("correlationId", out var cid))
            result = result.Replace("{correlationId}", cid ?? string.Empty);
        return result;
    }
}
