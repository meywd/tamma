using System.Text.Json;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Rules;

/// <summary>
/// Story 5.6 (Wave C.2) — context passed to
/// <see cref="IAlertRule.EvaluateAsync"/> and the predicate AST. Bundles
/// the triggering <see cref="Event"/>, the rolling-window store, and
/// lazily-parsed views over the event's <c>Tags</c> / <c>Data</c> JSON
/// blobs so predicates don't re-parse on every evaluation.
/// </summary>
public sealed class AlertRuleContext
{
    private IReadOnlyDictionary<string, string?>? _tagsCache;
    private JsonDocument? _dataDoc;
    private bool _dataParsed;

    public AlertRuleContext(
        Guid ruleId,
        DomainEvent @event,
        IRuleWindowStore windowStore)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(windowStore);
        RuleId = ruleId;
        Event = @event;
        WindowStore = windowStore;
    }

    public Guid RuleId { get; }
    public DomainEvent Event { get; }
    public IRuleWindowStore WindowStore { get; }

    /// <summary>
    /// Tag value lookup. Parses <see cref="DomainEvent.Tags"/> JSON
    /// once and caches the dict for reuse across predicate nodes.
    /// Tenant id is materialised into the tag dict under
    /// <c>"tenantId"</c> even when the JSON blob omits it, so
    /// <c>count_gte.group_by=["tenantId"]</c> correlates as documented.
    /// The synthesised <c>"scope"</c> tag is always present:
    /// <c>"platform"</c> when <see cref="DomainEvent.TenantId"/> is
    /// null, <c>"tenant:&lt;guid-N&gt;"</c> otherwise. This lets
    /// <c>count_gte</c> partition platform events by scope (so two
    /// tenants' tenant-scoped events never collide on a missing
    /// <c>tenantId</c> tag, and platform-wide events still group
    /// globally under <c>scope=platform</c>).
    /// </summary>
    public bool TryGetTag(string key, out string? value)
    {
        value = null;
        _tagsCache ??= BuildTagsCache();
        return _tagsCache.TryGetValue(key, out value);
    }

    private IReadOnlyDictionary<string, string?> BuildTagsCache()
    {
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(Event.Tags) && Event.Tags != "{}")
        {
            try
            {
                using var doc = JsonDocument.Parse(Event.Tags);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        dict[prop.Name] = prop.Value.ValueKind switch
                        {
                            JsonValueKind.Null => null,
                            JsonValueKind.String => prop.Value.GetString(),
                            _ => prop.Value.GetRawText(),
                        };
                    }
                }
            }
            catch (JsonException)
            {
                // Malformed tags JSON — treat as empty. The sink
                // already validates tag shape on the emit path; an
                // unparseable tag blob is a bug elsewhere that
                // shouldn't crash rule evaluation.
            }
        }
        // Project DomainEvent.TenantId into the tag dict unless the
        // JSON payload already set it. Predicates that correlate by
        // tenantId do so against a single canonical key.
        if (!dict.ContainsKey("tenantId") && Event.TenantId.HasValue)
        {
            dict["tenantId"] = Event.TenantId.Value.ToString("N");
        }
        // Synthesise the "scope" tag. This partitions the count_gte
        // correlation domain between platform-wide events (TenantId ==
        // null) and tenant-scoped events. Without this, a count_gte
        // rule defaulting to group_by=["tenantId"] pooled every tenant's
        // platform-scoped events into one shared "(null)" bucket.
        // Event-supplied "scope" wins if the emitter already set it.
        if (!dict.ContainsKey("scope"))
        {
            dict["scope"] = Event.TenantId.HasValue
                ? $"tenant:{Event.TenantId.Value:N}"
                : "platform";
        }
        return dict;
    }

    /// <summary>
    /// Dotted-path lookup into <see cref="DomainEvent.Data"/> JSON.
    /// Returns the leaf value as a string (primitives) or the raw
    /// subtree text (objects/arrays). Missing path → false.
    /// </summary>
    public bool TryGetDataField(string path, out string? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!_dataParsed)
        {
            _dataParsed = true;
            if (!string.IsNullOrWhiteSpace(Event.Data) && Event.Data != "{}")
            {
                try { _dataDoc = JsonDocument.Parse(Event.Data); }
                catch (JsonException) { _dataDoc = null; }
            }
        }
        if (_dataDoc is null) return false;

        var el = _dataDoc.RootElement;
        foreach (var segment in path.Split('.'))
        {
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (!el.TryGetProperty(segment, out var next)) return false;
            el = next;
        }

        value = el.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => el.GetRawText(),
        };
        return true;
    }

    /// <summary>
    /// Compute the group-by correlation key from a list of tag keys.
    /// Empty list = global bucket. Missing tag = literal
    /// <c>"(null)"</c> so two events with the same missing tag still
    /// correlate.
    /// </summary>
    public string ComputeGroupKey(IReadOnlyList<string> groupBy)
    {
        if (groupBy.Count == 0) return string.Empty;
        var parts = new List<string>(groupBy.Count);
        foreach (var k in groupBy)
        {
            TryGetTag(k, out var v);
            parts.Add(v ?? "(null)");
        }
        return string.Join("|", parts);
    }
}
