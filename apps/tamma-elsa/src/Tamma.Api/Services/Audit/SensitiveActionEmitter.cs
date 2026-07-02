using System.Text.Json;
using Tamma.Core.Audit;
using Tamma.Core.Redaction;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-10 — the single curated sensitive-action emitter. Validates the
/// event type against <see cref="SensitiveActionCatalog"/>, defensively strips
/// secret-shaped values, and routes to the correct DCB stream:
/// <list type="bullet">
///   <item><see cref="SensitiveActionScope.Tenant"/> → the tenant's
///     <c>domain_events</c> via <see cref="IEventRepository"/>.</item>
///   <item><see cref="SensitiveActionScope.Platform"/> → <c>platform_events</c>
///     via <see cref="IPlatformEventPublisher"/> (TenantId optional).</item>
/// </list>
/// Never throws to the caller (mirrors <c>ISecretAccessAuditor</c>).
/// </summary>
public sealed class SensitiveActionEmitter : ISensitiveActionEmitter
{
    // Repo convention (matches OrgEndpoints.EmitTenantEvent / BuildLifecycleEvent).
    private const string MetadataJson = """{"workflowVersion":"1.0.0","eventSource":"system"}""";

    /// <summary>Tag/data keys whose VALUE is presumed secret and is scrubbed to a
    /// placeholder regardless of shape (belt-and-suspenders — callers should never
    /// pass these, but a caller bug must not leak a secret into the audit trail).</summary>
    private static readonly HashSet<string> DeniedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "token", "password", "passwd", "pwd", "secret",
        "connectionstring", "card", "cardnumber", "plaintext", "initialplaintext",
        "authorization",
    };

    private readonly IEventRepository _events;
    private readonly IPlatformEventPublisher _platform;
    private readonly TimeProvider _time;
    private readonly ILogger<SensitiveActionEmitter> _logger;

    public SensitiveActionEmitter(
        IEventRepository events,
        IPlatformEventPublisher platform,
        TimeProvider time,
        ILogger<SensitiveActionEmitter> logger)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _events = events;
        _platform = platform;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task EmitAsync(SensitiveAction action, CancellationToken ct = default)
    {
        if (action is null)
        {
            _logger.LogWarning("SensitiveActionEmitter received a null action; dropped.");
            return;
        }

        // Typo guard (AC — validation): an un-cataloged Type is logged + dropped,
        // never thrown, and never emitted (the projector would skip it anyway, but
        // dropping here keeps the raw stream free of un-cataloged "audit" events).
        if (!SensitiveActionCatalog.IsSensitive(action.Type))
        {
            _logger.LogWarning(
                "SensitiveActionEmitter dropped an event: type '{Type}' is not in the "
                + "SensitiveActionCatalog (typo guard).", action.Type);
            return;
        }

        try
        {
            var tags = BuildTags(action);
            var data = Redact(action.Data);

            if (action.Scope == SensitiveActionScope.Tenant)
            {
                if (action.TenantId is not Guid tenantId || tenantId == Guid.Empty)
                {
                    _logger.LogWarning(
                        "SensitiveActionEmitter dropped a tenant-scoped '{Type}' event: no TenantId.",
                        action.Type);
                    return;
                }

                await _events.AppendAsync(new DomainEvent
                {
                    Id = Guid.NewGuid(),
                    Type = action.Type,
                    TenantId = tenantId,
                    Tags = JsonSerializer.Serialize(tags),
                    Metadata = MetadataJson,
                    Data = JsonSerializer.Serialize(data),
                    CreatedAt = _time.GetUtcNow().UtcDateTime,
                }).ConfigureAwait(false);
            }
            else
            {
                await _platform.AppendAndPublishAsync(new PlatformEvent
                {
                    Id = Guid.NewGuid(),
                    Type = action.Type,
                    // A platform event MAY carry a tenant id — the projector then
                    // materialises the curated row into that tenant's schema (SaaS).
                    TenantId = action.TenantId,
                    UserId = action.ActorUserId,
                    Tags = JsonSerializer.Serialize(tags),
                    Metadata = MetadataJson,
                    Data = JsonSerializer.Serialize(data),
                    CreatedAt = _time.GetUtcNow().UtcDateTime,
                }, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Sensitive-action emitted type={Type} scope={Scope} tenantId={TenantId} actor={Actor}",
                action.Type, action.Scope,
                action.TenantId?.ToString("D") ?? "(platform)",
                action.ActorUserId?.ToString("D") ?? "(none)");
        }
        catch (Exception ex)
        {
            // Never-throws contract: an audit-sink outage must NOT roll back the
            // action that already happened. Log loudly and swallow.
            _logger.LogError(ex,
                "SensitiveActionEmitter sink write failed for type={Type} scope={Scope}; "
                + "the action is NOT rolled back (audit emission is best-effort).",
                action.Type, action.Scope);
        }
    }

    /// <summary>
    /// Assemble the tag map: caller tags (redacted) plus the actor/tenant keys the
    /// Story 37-1 projector resolves (<c>actorUserId</c>, <c>tenantId</c>). Caller
    /// tags win when present so a site can override (e.g. a login-failure has no
    /// trusted actor).
    /// </summary>
    private static Dictionary<string, string?> BuildTags(SensitiveAction action)
    {
        var tags = new Dictionary<string, string?>(StringComparer.Ordinal);

        if (action.ActorUserId is Guid actor && actor != Guid.Empty)
            tags["actorUserId"] = actor.ToString("D");
        if (action.TenantId is Guid tenant && tenant != Guid.Empty)
            tags["tenantId"] = tenant.ToString("D");

        foreach (var (key, value) in action.Tags)
        {
            if (string.IsNullOrEmpty(key)) continue;
            tags[key] = DeniedKeys.Contains(key)
                ? CredentialRedactor.Placeholder
                : Scrub(value);
        }

        return tags;
    }

    /// <summary>Redact the data payload: denylisted keys → placeholder; string
    /// values → credential-scrubbed; other values passed through.</summary>
    private static Dictionary<string, object?> Redact(IReadOnlyDictionary<string, object?> data)
    {
        var clean = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (DeniedKeys.Contains(key))
            {
                clean[key] = CredentialRedactor.Placeholder;
                continue;
            }
            clean[key] = value is string s ? Scrub(s) : value;
        }
        return clean;
    }

    /// <summary>Run the shared credential redactor over a string value. Null stays
    /// null; a secret-shaped value (e.g. <c>tamma_sk_…</c>) becomes
    /// <c>[REDACTED]</c>; an ordinary value is returned unchanged.</summary>
    private static string? Scrub(string? value) =>
        string.IsNullOrEmpty(value) ? value : CredentialRedactor.Clean(value);
}
