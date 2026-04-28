using System.Text.Json;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — parsed + validated predicate AST.
///
/// <para>DSL grammar:</para>
/// <code>
///   predicate := { "op": "always" }
///              | { "op": "count_gte", "window_seconds": N, "threshold": K,
///                  "group_by": ["tenantId", ...]? }
///              | { "op": "and", "clauses": [predicate, ...] }
///              | { "op": "or",  "clauses": [predicate, ...] }
///              | { "op": "tag_eq", "key": "...", "value": "..." }
///              | { "op": "data_field_eq", "path": "foo.bar", "value": "..." }
/// </code>
///
/// <para>The root node may also declare a top-level
/// <c>"group_by"</c> array (for rules that use <c>count_gte</c>
/// anywhere in the tree) — only honoured when attached to a
/// <c>count_gte</c> node. Parsing a malformed predicate throws an
/// <see cref="InvalidAlertRulePredicateException"/>.</para>
/// </summary>
public abstract record AlertRulePredicate
{
    /// <summary>
    /// Evaluate this predicate. Uses <paramref name="ctx"/> for the
    /// event payload + rolling-window counter for <c>count_gte</c>.
    /// </summary>
    public abstract bool Evaluate(AlertRuleContext ctx);

    /// <summary>Match every event.</summary>
    public sealed record Always : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx) => true;
    }

    /// <summary>
    /// Match when <see cref="Threshold"/> or more events of the rule's
    /// event type land within <see cref="WindowSeconds"/>, correlated
    /// by <see cref="GroupBy"/> (default <c>["scope", "tenantId"]</c>).
    /// The current event counts toward the threshold. The
    /// <c>scope</c> tag (synthesised by
    /// <see cref="AlertRuleContext"/>) partitions platform-wide events
    /// (<c>scope="platform"</c>) from tenant-scoped events
    /// (<c>scope="tenant:&lt;guid&gt;"</c>) so a platform-typed rule
    /// never pools all tenants into a shared "(null)" bucket.
    /// </summary>
    public sealed record CountGte(
        int WindowSeconds,
        int Threshold,
        IReadOnlyList<string> GroupBy) : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx)
        {
            var groupKey = ctx.ComputeGroupKey(GroupBy);
            var eventTime = ctx.Event.CreatedAt;
            var window = TimeSpan.FromSeconds(WindowSeconds);

            // Record the new occurrence and get the count within the
            // window. The store trims expired timestamps on each call
            // so the bucket stays bounded.
            var count = ctx.WindowStore.RecordAndCount(
                ctx.RuleId, groupKey, eventTime, window);
            return count >= Threshold;
        }
    }

    /// <summary>Logical AND over child clauses.</summary>
    public sealed record And(IReadOnlyList<AlertRulePredicate> Clauses)
        : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx)
        {
            // short-circuit — evaluate in-order, stop on first false.
            foreach (var c in Clauses)
                if (!c.Evaluate(ctx)) return false;
            return Clauses.Count > 0;  // empty AND is false by convention
        }
    }

    /// <summary>Logical OR over child clauses.</summary>
    public sealed record Or(IReadOnlyList<AlertRulePredicate> Clauses)
        : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx)
        {
            foreach (var c in Clauses)
                if (c.Evaluate(ctx)) return true;
            return false;  // empty OR is false
        }
    }

    /// <summary>Tag key equals literal value.</summary>
    public sealed record TagEq(string Key, string Value) : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx) =>
            ctx.TryGetTag(Key, out var v) && string.Equals(v, Value, StringComparison.Ordinal);
    }

    /// <summary>Data JSON field (dotted path) equals literal value.</summary>
    public sealed record DataFieldEq(string Path, string Value)
        : AlertRulePredicate
    {
        public override bool Evaluate(AlertRuleContext ctx) =>
            ctx.TryGetDataField(Path, out var v) && string.Equals(v, Value, StringComparison.Ordinal);
    }
}

/// <summary>
/// Parse + validate a predicate JSON blob into an
/// <see cref="AlertRulePredicate"/> AST. Single entry-point so tests
/// and endpoints share one validation surface.
/// </summary>
public static class AlertRulePredicateParser
{
    /// <summary>
    /// Parse the predicate. Throws
    /// <see cref="InvalidAlertRulePredicateException"/> on grammar
    /// violation.
    /// </summary>
    public static AlertRulePredicate Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}")
        {
            throw new InvalidAlertRulePredicateException(
                "$", "predicate is required (at minimum {\"op\":\"always\"}).");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidAlertRulePredicateException(
                "$", $"not valid JSON: {ex.Message}");
        }
        using (doc)
        {
            return ParseNode(doc.RootElement, "$");
        }
    }

    private static AlertRulePredicate ParseNode(JsonElement el, string path)
    {
        if (el.ValueKind != JsonValueKind.Object)
            throw new InvalidAlertRulePredicateException(
                path, $"expected object, got {el.ValueKind}.");

        if (!el.TryGetProperty("op", out var opEl) ||
            opEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.op",
                "missing or non-string 'op' field.");
        }

        var op = opEl.GetString()!;
        return op switch
        {
            "always" => new AlertRulePredicate.Always(),
            "count_gte" => ParseCountGte(el, path),
            "and" => ParseAnd(el, path),
            "or" => ParseOr(el, path),
            "tag_eq" => ParseTagEq(el, path),
            "data_field_eq" => ParseDataFieldEq(el, path),
            _ => throw new InvalidAlertRulePredicateException(
                $"{path}.op",
                $"unknown op '{op}'. Supported: always, count_gte, and, " +
                "or, tag_eq, data_field_eq."),
        };
    }

    private static AlertRulePredicate.CountGte ParseCountGte(
        JsonElement el, string path)
    {
        if (!el.TryGetProperty("window_seconds", out var wEl) ||
            wEl.ValueKind != JsonValueKind.Number || !wEl.TryGetInt32(out var window) ||
            window <= 0)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.window_seconds",
                "required positive integer (seconds).");
        }
        if (!el.TryGetProperty("threshold", out var tEl) ||
            tEl.ValueKind != JsonValueKind.Number || !tEl.TryGetInt32(out var threshold) ||
            threshold <= 0)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.threshold",
                "required positive integer (count).");
        }

        // Default correlation partitions on scope first, then
        // tenantId. "scope" is always present in the tag dict
        // ("platform" | "tenant:<guid-N>") so platform-scoped events
        // (TenantId == null) group under "platform|(null)" and
        // tenant-scoped events group under "tenant:<g>|<g>" — no
        // cross-tenant collision on a missing tenantId tag.
        var groupBy = new List<string> { "scope", "tenantId" };
        if (el.TryGetProperty("group_by", out var gEl))
        {
            if (gEl.ValueKind != JsonValueKind.Array)
                throw new InvalidAlertRulePredicateException(
                    $"{path}.group_by", "expected array of strings.");
            groupBy.Clear();
            var i = 0;
            foreach (var item in gEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidAlertRulePredicateException(
                        $"{path}.group_by[{i}]", "expected string.");
                groupBy.Add(item.GetString()!);
                i++;
            }
            if (groupBy.Count == 0)
            {
                // Empty list = no correlation — single global bucket.
                // Permitted but explicit.
            }
        }
        return new AlertRulePredicate.CountGte(window, threshold, groupBy);
    }

    private static AlertRulePredicate.And ParseAnd(JsonElement el, string path) =>
        new(ParseClauses(el, path, "and"));

    private static AlertRulePredicate.Or ParseOr(JsonElement el, string path) =>
        new(ParseClauses(el, path, "or"));

    private static List<AlertRulePredicate> ParseClauses(
        JsonElement el, string path, string label)
    {
        if (!el.TryGetProperty("clauses", out var cEl) ||
            cEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.clauses",
                $"'{label}' requires a non-empty 'clauses' array.");
        }
        var list = new List<AlertRulePredicate>();
        var i = 0;
        foreach (var child in cEl.EnumerateArray())
        {
            list.Add(ParseNode(child, $"{path}.clauses[{i}]"));
            i++;
        }
        if (list.Count == 0)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.clauses",
                $"'{label}' requires at least one clause.");
        }
        return list;
    }

    private static AlertRulePredicate.TagEq ParseTagEq(
        JsonElement el, string path)
    {
        var key = RequireStringField(el, path, "key");
        var value = RequireStringField(el, path, "value");
        return new AlertRulePredicate.TagEq(key, value);
    }

    private static AlertRulePredicate.DataFieldEq ParseDataFieldEq(
        JsonElement el, string path)
    {
        var field = RequireStringField(el, path, "path");
        var value = RequireStringField(el, path, "value");
        return new AlertRulePredicate.DataFieldEq(field, value);
    }

    private static string RequireStringField(
        JsonElement el, string path, string field)
    {
        if (!el.TryGetProperty(field, out var fEl) ||
            fEl.ValueKind != JsonValueKind.String)
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.{field}", "required non-empty string.");
        }
        var v = fEl.GetString()!;
        if (string.IsNullOrEmpty(v))
        {
            throw new InvalidAlertRulePredicateException(
                $"{path}.{field}", "must not be empty.");
        }
        return v;
    }
}
