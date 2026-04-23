namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — one instance per alert rule row, refreshed
/// by <see cref="IAlertRuleRegistry"/> on reload. The evaluator pipes
/// each matching <see cref="Tamma.Data.Entities.DomainEvent"/>
/// through <see cref="Evaluate"/>; a non-null return value is handed
/// to <see cref="IAlertSink.RaiseAsync"/>.
///
/// <para>Rules are stateless except for the rolling-window counter
/// on <see cref="AlertRuleContext.WindowStore"/>. In-process throttle
/// is tracked by the evaluator, not the rule itself.</para>
/// </summary>
public interface IAlertRule
{
    /// <summary>Stable rule id (FK into <c>alert_rules</c>).</summary>
    Guid Id { get; }

    /// <summary>The DCB event type the rule subscribes to.</summary>
    string EventType { get; }

    /// <summary>
    /// Throttle window in seconds — enforced by the evaluator in
    /// addition to the sink-side rate limiter. 0 = no throttle.
    /// </summary>
    int ThrottleSeconds { get; }

    /// <summary>
    /// Evaluate the rule against a matching event. Returns a payload
    /// when the rule fires; returns null when the predicate doesn't
    /// match. Must not throw on normal flow — a bad predicate is
    /// caught at load time, not eval time.
    /// </summary>
    AlertPayload? Evaluate(AlertRuleContext ctx);
}
