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
/// <item><b>An explicit UTC expiry is MANDATORY, and BOUNDED.</b> Missing,
/// unparseable, already-past, or more than
/// <see cref="MaximumDuration"/> (<b>24 hours</b>) away ⇒ the override REFUSES TO
/// ENGAGE and says so at ERROR. A break-glass that can be left on forever stops
/// being a break-glass and becomes the permanent configuration — which is the
/// fail-open the F6 close removed. The cap is what makes "mandatory expiry" mean
/// something: without it, <c>9999-12-31T23:59:59Z</c> satisfied every check and
/// produced exactly the "left on forever" outcome the expiry exists to prevent
/// (review MEDIUM-3, 2026-07-31). <b>Why 24 hours specifically:</b> break-glass
/// is an OUTAGE lever, and an outage still unresolved after a day needs a real
/// fix rather than a longer bypass. Re-engaging past the cap is deliberately a
/// configuration change plus a restart — the friction is the mechanism by which
/// a second day of bypass becomes somebody's explicit decision instead of a
/// timestamp nobody re-read.</item>
/// <item><b>Loud at every transition, and fail-closed on every malformed
/// input.</b> ERROR on engage, ERROR on refusal, ERROR on expiry. (The
/// per-DECISION ERROR log and the per-decision audit event belong to the gates —
/// see <c>AutonomyGateService</c> and <c>CatalogDefaultToolLoopAutonomyGate</c>.)
/// A quiet break-glass is the fail-open with extra steps. That includes a
/// malformed <c>Enabled</c> value: <c>GetValue&lt;bool&gt;</c> throws on
/// <c>"yes"</c>, and because this type is built in a DI factory that surfaced as
/// a service-resolution failure on the first gate call rather than as a refusal
/// (review INFO-8) — it is now caught, logged at ERROR, and read as NOT
/// engaged.</item>
/// </list>
///
/// <para><b>Scope reminder — this is NOT an off switch for policy.</b> What it
/// suspends is the substitution of <c>AlwaysHuman</c> for an UNREADABLE
/// governance input. A decision denied by a policy row that was read
/// successfully stays denied. That boundary lives in
/// <see cref="AutonomyGateEvaluator"/>, not here.</para>
///
/// <para><b>DISENGAGING ALSO REQUIRES A RESTART — state it plainly (review
/// INFO-7, 2026-07-31).</b> "Engaging requires a restart" was documented; the
/// symmetric fact was not. <c>_configured</c> is captured in the CONSTRUCTOR, so
/// setting <c>Enabled = false</c> and reloading configuration does NOT disengage
/// a running process. An operator who engages the override by mistake — or who
/// fixes the outage in ten minutes — <b>cannot turn it off before the expiry
/// without restarting the process</b>. That is the direct cost of decision (2),
/// and it is the reason the expiry cap in (3) is short: the expiry is the only
/// in-process off switch there is.</para>
///
/// <para>Configuration keys (all under <c>Tamma:Governance:BreakGlass</c>):
/// <c>Enabled</c> (bool), <c>ExpiresAtUtc</c> (ISO-8601 instant, treated as UTC
/// when no offset is given; at most <see cref="MaximumDuration"/> away),
/// <c>Reason</c> (free text, carried into every audit row).</para>
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

    /// <summary>
    /// The longest window the override will engage for: <b>24 hours</b> (review
    /// MEDIUM-3, 2026-07-31). An expiry further out than this is REFUSED and
    /// logged at ERROR, exactly like a missing one.
    ///
    /// <para>Break-glass is an outage lever. An outage still unresolved after a
    /// day does not need a longer bypass, it needs a fix — and if the answer
    /// genuinely is "another day", that should cost a deliberate configuration
    /// change plus a restart rather than being pre-authorised by a timestamp
    /// somebody typed once. Without this bound the mandatory expiry was
    /// satisfiable by <c>9999-12-31T23:59:59Z</c>, i.e. by nothing at all.</para>
    /// </summary>
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromHours(24);

    private readonly TimeProvider _time;
    private readonly ILogger<ConfigurationGovernanceBreakGlass>? _logger;

    /// <summary>Null when the configuration did not engage a valid override.</summary>
    private readonly BreakGlassState? _configured;

    private int _expiryLogged;

    /// <summary>
    /// LOW-4 — once this process has observed the expiry pass, the override is
    /// over for the lifetime of the process. Expiry used to be re-derived from the
    /// clock on every call with nothing remembered, so a backwards clock step
    /// (NTP correction, a resumed VM, a bad RTC) RE-ENGAGED it — and re-engaged it
    /// silently, because the "expired" ERROR is one-shot and the "engaged" ERROR
    /// is constructor-only. Time going forwards is the only thing that may end an
    /// override; nothing may restart one.
    /// </summary>
    private int _expired;

    public ConfigurationGovernanceBreakGlass(
        IConfiguration configuration,
        ILogger<ConfigurationGovernanceBreakGlass>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _time = timeProvider ?? TimeProvider.System;
        _logger = logger;

        // INFO-8 — a malformed flag FAILS CLOSED. GetValue<bool> throws on
        // anything that is not a bool literal ("yes", "1", "on"), and this type is
        // constructed inside a DI factory, so that exception used to escape as a
        // service-resolution failure at the FIRST GATE CALL: no startup refusal,
        // no ERROR line, just an unrelated-looking 500. A governance switch that
        // cannot be read is not on.
        bool enabled;
        try
        {
            enabled = configuration.GetValue(EnabledKey, defaultValue: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            _logger?.LogError(ex,
                "GOVERNANCE BREAK-GLASS REFUSED: '{EnabledKey}' is not a boolean ('true' or "
                + "'false'). The break-glass override will NOT engage; the fail-closed "
                + "governance posture stays in force.",
                EnabledKey);
            _configured = null;
            return;
        }

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

        var now = _time.GetUtcNow();
        if (expiresAt <= now)
        {
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS REFUSED: '{ExpiresKey}' ({ExpiresAt:O}) is already in the "
                + "past. The break-glass override will NOT engage; the fail-closed governance "
                + "posture stays in force.",
                ExpiresAtUtcKey, expiresAt);
            _configured = null;
            return;
        }

        // MEDIUM-3 — the UPPER bound. "In the future" is not a constraint; a
        // year-9999 expiry passed every check above and engaged permanently.
        if (expiresAt - now > MaximumDuration)
        {
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS REFUSED: '{ExpiresKey}' ({ExpiresAt:O}) is {Requested} "
                + "away, beyond the {Maximum} maximum. Break-glass is an OUTAGE lever: an outage "
                + "still unresolved after that long needs a fix, not a longer bypass. Set a "
                + "nearer expiry and restart; the fail-closed governance posture stays in force "
                + "until you do.",
                ExpiresAtUtcKey, expiresAt, expiresAt - now, MaximumDuration);
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
        if (_configured is null || Volatile.Read(ref _expired) == 1)
        {
            // LOW-4 — the latch is checked BEFORE the clock, so an override this
            // process has already seen end can never come back, whatever the clock
            // subsequently says.
            return BreakGlassState.NotEngaged;
        }

        if (_configured.ExpiresAtUtc is DateTimeOffset expiry && _time.GetUtcNow() >= expiry)
        {
            Volatile.Write(ref _expired, 1);
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
