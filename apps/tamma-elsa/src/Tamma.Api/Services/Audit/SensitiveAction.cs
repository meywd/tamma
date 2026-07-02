namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-10 — which DCB stream a curated sensitive-action event is appended
/// to. The <see cref="ISensitiveActionEmitter"/> makes exactly one routing
/// decision, in one tested place, keyed off this value (never duplicated at the
/// 12 call sites).
/// </summary>
public enum SensitiveActionScope
{
    /// <summary>Tenant-scoped action (BYOK, plan, persona/config, tenant agent
    /// action, tenant export). Appends to the tenant's <c>domain_events</c>
    /// stream via <c>IEventRepository</c> with <c>TenantId</c> set. The Story
    /// 37-1 projector materialises it into that tenant's <c>audit_records</c>.</summary>
    Tenant,

    /// <summary>Control-plane / platform-edge action (login success/failure,
    /// token refresh, API-key auth, impersonation, system-scope config). Appends
    /// to <c>platform_events</c> via <c>IPlatformEventPublisher</c>. A platform
    /// event MAY still carry a <c>TenantId</c> — the projector then routes the
    /// curated row to that tenant's schema (SaaS); a null <c>TenantId</c> keeps
    /// it in the control plane, never exposed to a tenant.</summary>
    Platform,
}

/// <summary>
/// Story 37-10 — one curated sensitive-action event to append to the DCB store
/// so the Story 37-1 <c>AuditProjector</c> can materialise it into
/// <c>audit_records</c>. <see cref="Type"/> MUST be a code in
/// <c>SensitiveActionCatalog</c>; the emitter validates and drops (never throws)
/// an un-cataloged type so a typo cannot silently create an un-cataloged event.
///
/// <para><b>Redaction-safe payload only.</b> <see cref="Tags"/> / <see cref="Data"/>
/// carry metadata (provider, mode, version, ip, reason, ...), never key material,
/// plaintext secrets, card data, or a full API key. The emitter additionally runs
/// a defensive strip (belt-and-suspenders on top of the projector's redaction).</para>
/// </summary>
/// <param name="Type">Canonical DCB event-type string — a <c>SensitiveActionCatalog</c> code.</param>
/// <param name="Scope">Tenant <c>domain_events</c> vs platform <c>platform_events</c>.</param>
/// <param name="TenantId">Tenant owner. Required for <see cref="SensitiveActionScope.Tenant"/>;
/// optional for <see cref="SensitiveActionScope.Platform"/> (null = control-plane only).</param>
/// <param name="ActorUserId">The acting user id, when known.</param>
/// <param name="Tags">Filterable tags (projector reads <c>actorUserId</c>/<c>userId</c>,
/// <c>ip</c>, <c>userAgent</c>, <c>tenantId</c>, plus site-specific keys).</param>
/// <param name="Data">Redaction-safe event payload (metadata only).</param>
public sealed record SensitiveAction(
    string Type,
    SensitiveActionScope Scope,
    Guid? TenantId,
    Guid? ActorUserId,
    IReadOnlyDictionary<string, string?> Tags,
    IReadOnlyDictionary<string, object?> Data)
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyTags =
        new Dictionary<string, string?>(0);
    private static readonly IReadOnlyDictionary<string, object?> EmptyData =
        new Dictionary<string, object?>(0);

    /// <summary>Build a tenant-scoped action (routes to <c>domain_events</c>).</summary>
    public static SensitiveAction ForTenant(
        string type,
        Guid tenantId,
        Guid? actorUserId,
        IReadOnlyDictionary<string, string?>? tags = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(type, SensitiveActionScope.Tenant, tenantId, actorUserId,
            tags ?? EmptyTags, data ?? EmptyData);

    /// <summary>Build a platform-edge action (routes to <c>platform_events</c>).
    /// <paramref name="tenantId"/> is optional — set it when the action is about a
    /// tenant (so the curated row lands in that tenant's schema in SaaS), null for
    /// a purely control-plane action.</summary>
    public static SensitiveAction ForPlatform(
        string type,
        Guid? tenantId,
        Guid? actorUserId,
        IReadOnlyDictionary<string, string?>? tags = null,
        IReadOnlyDictionary<string, object?>? data = null) =>
        new(type, SensitiveActionScope.Platform, tenantId, actorUserId,
            tags ?? EmptyTags, data ?? EmptyData);
}
