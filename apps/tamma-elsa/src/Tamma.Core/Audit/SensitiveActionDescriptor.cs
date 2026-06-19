namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-1 — the classification record for one catalogued sensitive
/// action. The <see cref="SensitiveActionCatalog"/> maps every catalogued
/// DCB event-type string to exactly one of these. It is the single source of
/// truth for "is this raw event a sensitive action, and how is it classified".
/// </summary>
/// <param name="ActionCode">Canonical DCB event-type string, e.g.
/// <c>SECRET.REVEAL</c>. This is the key the projector matches a raw
/// <c>DomainEvent</c>/<c>PlatformEvent</c>'s <c>Type</c> against.</param>
/// <param name="Category">Compliance category.</param>
/// <param name="Severity">Coarse triage severity.</param>
/// <param name="Soc2ControlId">SOC2 Trust-Services-Criteria control id this
/// action provides evidence for, e.g. <c>CC6.1</c>. Never empty.</param>
/// <param name="TargetTypeHint">Default target-type label written to the
/// curated record when the raw event doesn't carry one, e.g. <c>secret</c>,
/// <c>user</c>, <c>tenant</c>.</param>
/// <param name="MapsExistingEmitter"><c>true</c> when this code maps an event
/// type a real emitter already appends to the DCB store today (verified by
/// grep at authoring time); <c>false</c> when it is a forward-looking
/// taxonomy entry for an action not yet emitted. The catalog-completeness
/// test pins the <c>true</c> set against the actual emitter constants so a
/// future rename that drops an emitted type from the catalog fails CI.</param>
public sealed record SensitiveActionDescriptor(
    string ActionCode,
    AuditCategory Category,
    AuditSeverity Severity,
    string Soc2ControlId,
    string TargetTypeHint,
    bool MapsExistingEmitter);
