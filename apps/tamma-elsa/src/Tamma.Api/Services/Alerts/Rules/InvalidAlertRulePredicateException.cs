namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — thrown by <c>AlertRulePredicateParser</c>
/// when a stored predicate JSON blob violates the DSL grammar (unknown
/// <c>op</c>, missing required field, type mismatch). The endpoint
/// layer translates this into a 400 Bad Request with a structured
/// body carrying the <see cref="FieldPath"/> so authors can fix the
/// precise field.
/// </summary>
public sealed class InvalidAlertRulePredicateException : Exception
{
    /// <summary>
    /// JSON-pointer-ish path into the offending field (e.g.
    /// <c>"clauses[2].window_seconds"</c>).
    /// </summary>
    public string FieldPath { get; }

    public InvalidAlertRulePredicateException(string fieldPath, string reason)
        : base($"Invalid alert rule predicate at '{fieldPath}': {reason}")
    {
        FieldPath = fieldPath;
    }
}
