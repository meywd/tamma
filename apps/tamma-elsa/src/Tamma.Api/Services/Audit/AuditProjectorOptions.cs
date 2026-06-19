namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-1 — options for <see cref="AuditProjectorBackgroundService"/>.
/// Mirrors <c>AlertRuleEvaluatorOptions</c>.
/// </summary>
public sealed class AuditProjectorOptions
{
    /// <summary>How often the projector polls for new DCB events. Default 5s —
    /// audit materialization is eventual (AC9), not real-time.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Rows scanned per stream per tick.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Stable id for this logical projector — one row per id in
    /// <c>audit_projector_cursor</c>.</summary>
    public string ProjectorId { get; set; } = "default";

    /// <summary>
    /// When <c>true</c> the projector's polling loop runs once the host starts.
    /// <b>Default is <c>false</c></b> so the background loop does NOT run during
    /// the test suite (and during deployments that have not opted in) and cause
    /// interference/flakiness. Production opts in explicitly. Tests that drive
    /// <see cref="AuditProjectorBackgroundService.ProcessOnceAsync"/> directly
    /// never need the loop. Mirrors the <c>RunOnStartup</c> gate on
    /// <c>AlertRuleEvaluatorOptions</c> (which defaults true), but flipped to a
    /// safe-off default per the Story 37-1 brief.
    /// </summary>
    public bool RunOnStartup { get; set; }

    /// <summary>Lag (in events) above which a WARN is logged. Default 10 000.</summary>
    public long LagWarnThreshold { get; set; } = 10_000;
}
