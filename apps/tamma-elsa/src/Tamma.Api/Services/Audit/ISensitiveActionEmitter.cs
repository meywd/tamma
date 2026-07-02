namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-10 — the single seam every sensitive-action call site emits through
/// so the curated DCB event has one shape and the scope-routing decision lives
/// in one tested place.
///
/// <para><b>Never throws to the caller.</b> An audit-sink outage must NOT roll
/// back the action that already happened (a failed login-audit must not turn a
/// successful login into a 500) — this mirrors <c>ISecretAccessAuditor</c>. The
/// implementation logs and swallows; the action is authoritative, the audit
/// emission is a best-effort side effect.</para>
///
/// <para><b>Catalog-validated.</b> A <see cref="SensitiveAction.Type"/> that is
/// not a <c>SensitiveActionCatalog</c> code is logged (WARN) and dropped so a
/// typo cannot silently create an un-cataloged event the projector would never
/// materialise.</para>
/// </summary>
public interface ISensitiveActionEmitter
{
    /// <summary>
    /// Append a curated sensitive-action event. Routes to the tenant
    /// <c>domain_events</c> stream (<c>IEventRepository</c>) for
    /// <see cref="SensitiveActionScope.Tenant"/>, or to <c>platform_events</c>
    /// (<c>IPlatformEventPublisher</c>) for <see cref="SensitiveActionScope.Platform"/>.
    /// Never throws.
    /// </summary>
    Task EmitAsync(SensitiveAction action, CancellationToken ct = default);
}
