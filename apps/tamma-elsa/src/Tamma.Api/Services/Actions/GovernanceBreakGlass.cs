using System.Globalization;
using Tamma.Core.Actions;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// The source of the break-glass override state for this process (Story 43-5
/// follow-up <b>F11</b>, closed 2026-07-30; it was recorded as a BLOCKER on
/// Story 43-9).
/// </summary>
public interface IGovernanceBreakGlass
{
    /// <summary>
    /// The override's state right now. Cheap and side-effect-free apart from
    /// one-shot ERROR logging on the engage/expire transitions — gates call it
    /// on every evaluation.
    /// </summary>
    BreakGlassState Current();
}

/// <summary>
/// THE break-glass override, sourced from CONFIGURATION ONLY (F11 close,
/// 2026-07-30). Four product decisions are encoded here rather than described
/// somewhere:
///
/// <list type="number">
/// <item><b>Configuration, never an API endpoint.</b> An endpoint that can switch
/// off a governance posture is itself a governance surface — it would need its own
/// permission, its own audit, its own ceiling, and a compromised admin session
/// would reach it. Configuration requires a deploy or a restart; that friction is
/// the point, not an accident of implementation. There is deliberately no writer
/// on this type.</item>
/// <item><b>Read ONCE, at construction.</b> The keys are captured when the
/// singleton is built, so a reloading configuration provider cannot flip the
/// posture underneath a running process — engaging really does mean restarting.
/// Only the EXPIRY is re-evaluated per call, because expiry must be able to
/// arrive while the process runs.</item>
/// <item><b>An explicit UTC expiry is MANDATORY.</b> Missing, unparseable or
/// already-past ⇒ the override REFUSES TO ENGAGE and says so at ERROR. A
/// break-glass that can be left on forever stops being a break-glass and becomes
/// the permanent configuration — which is the fail-open the F6 close removed.</item>
/// <item><b>Loud at every transition.</b> ERROR on engage, ERROR on refusal,
/// ERROR on expiry. (The per-DECISION ERROR log and the per-decision audit event
/// belong to the gates — see <c>AutonomyGateService</c> and
/// <c>CatalogDefaultToolLoopAutonomyGate</c>.) A quiet break-glass is the
/// fail-open with extra steps.</item>
/// </list>
///
/// <para><b>Scope reminder — this is NOT an off switch for policy.</b> What it
/// suspends is the substitution of <c>AlwaysHuman</c> for an UNREADABLE
/// governance input. A decision denied by a policy row that was read
/// successfully stays denied. That boundary lives in
/// <see cref="AutonomyGateEvaluator"/>, not here.</para>
///
/// <para>Configuration keys (all under <c>Tamma:Governance:BreakGlass</c>):
/// <c>Enabled</c> (bool), <c>ExpiresAtUtc</c> (ISO-8601 instant, treated as UTC
/// when no offset is given), <c>Reason</c> (free text, carried into every audit
/// row).</para>
/// </summary>
public sealed class ConfigurationGovernanceBreakGlass : IGovernanceBreakGlass
{
    /// <summary>Config section root.</summary>
    public const string SectionKey = "Tamma:Governance:BreakGlass";

    /// <summary><c>Tamma:Governance:BreakGlass:Enabled</c>.</summary>
    public const string EnabledKey = SectionKey + ":Enabled";

    /// <summary><c>Tamma:Governance:BreakGlass:ExpiresAtUtc</c> — MANDATORY when enabled.</summary>
    public const string ExpiresAtUtcKey = SectionKey + ":ExpiresAtUtc";

    /// <summary><c>Tamma:Governance:BreakGlass:Reason</c>.</summary>
    public const string ReasonKey = SectionKey + ":Reason";

    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationGovernanceBreakGlass>? _logger;

    /// <summary>Null when the configuration did not engage a valid override.</summary>
    private readonly BreakGlassState? _configured;

    private int _expiryLogged;

    public ConfigurationGovernanceBreakGlass(
        IConfiguration configuration,
        ILogger<ConfigurationGovernanceBreakGlass>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;

        var enabled = configuration.GetValue(EnabledKey, defaultValue: false);
        if (!enabled)
        {
            _configured = null;
            return;
        }

        var rawExpiry = configuration[ExpiresAtUtcKey];
        var reason = configuration[ReasonKey];

        if (string.IsNullOrWhiteSpace(rawExpiry))
        {
            // REFUSE. An override with no end is the permanent configuration.
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS REFUSED: '{EnabledKey}' is true but '{ExpiresKey}' is "
                + "missing. The break-glass override requires an EXPLICIT UTC expiry and will "
                + "NOT engage without one; the fail-closed governance posture stays in force.",
                EnabledKey, ExpiresAtUtcKey);
            _configured = null;
            return;
        }

        if (!TryParseUtc(rawExpiry, out var expiresAt))
        {
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS REFUSED: '{ExpiresKey}' is not a parseable instant. "
                + "The break-glass override will NOT engage; the fail-closed governance posture "
                + "stays in force.",
                ExpiresAtUtcKey);
            _configured = null;
            return;
        }

        if (expiresAt <= _time.GetUtcNow())
        {
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS REFUSED: '{ExpiresKey}' ({ExpiresAt:O}) is already in the "
                + "past. The break-glass override will NOT engage; the fail-closed governance "
                + "posture stays in force.",
                ExpiresAtUtcKey, expiresAt);
            _configured = null;
            return;
        }

        _configured = BreakGlassState.Engaged(expiresAt, reason);
        _logger?.LogError(
            "GOVERNANCE BREAK-GLASS ENGAGED until {ExpiresAt:O} (reason: {Reason}). While engaged, "
            + "an autonomy-gate decision whose policy input could NOT BE READ proceeds on the "
            + "shipped/last-resolved threshold instead of failing closed. Decisions denied by a "
            + "policy row that WAS read are still denied. Every bypassed decision is logged at "
            + "ERROR and written to the audit stream as {EventType}.",
            expiresAt, _configured.ReasonOrUnspecified, ActionGateEventsService.BreakGlassBypassType);
    }

    /// <inheritdoc />
    public BreakGlassState Current()
    {
        if (_configured is null)
        {
            return BreakGlassState.NotEngaged;
        }

        if (_configured.ExpiresAtUtc is DateTimeOffset expiry && _time.GetUtcNow() >= expiry)
        {
            if (Interlocked.Exchange(ref _expiryLogged, 1) == 0)
            {
                _logger?.LogError(
                    "GOVERNANCE BREAK-GLASS EXPIRED at {ExpiresAt:O}; the fail-closed posture is "
                    + "back in force. Re-engaging requires a configuration change and a restart.",
                    expiry);
            }
            return BreakGlassState.NotEngaged;
        }

        return _configured;
    }

    /// <summary>
    /// Parse an expiry. A value carrying an offset is honoured; a bare instant is
    /// read as UTC (the key is named <c>ExpiresAtUtc</c>, so "assume UTC" is the
    /// reading that matches what the operator wrote — never local server time,
    /// which would make the same config mean different things on two hosts).
    /// </summary>
    internal static bool TryParseUtc(string raw, out DateTimeOffset expiresAt) =>
        DateTimeOffset.TryParse(
            raw,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out expiresAt);
}
