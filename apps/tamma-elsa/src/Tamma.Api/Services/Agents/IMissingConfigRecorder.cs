namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-2 — soft-dependency seam for the Missing-Config Notifications epic
/// (not yet merged). When a taxonomy-valid role resolves past every precedence
/// branch with no system default (AC 9), the resolver best-effort records a
/// <c>MISSING_CONFIG</c> gap so the future notifications pipeline can surface it.
///
/// <para><b>Injected OPTIONALLY</b> (<c>IMissingConfigRecorder?</c>): no default
/// registration ships in this story, so the constructor receives <c>null</c> and
/// the gap record is skipped. The mandatory side of the fail-loud path — the
/// <c>AGENT.RESOLVE.FAILED</c> DCB event PLUS the <c>TammaError</c> throw — fires
/// regardless. When the Missing-Config epic lands it registers an implementation
/// and the gap record begins flowing with zero changes here.</para>
/// </summary>
public interface IMissingConfigRecorder
{
    /// <summary>
    /// Record a missing-config gap. <paramref name="domain"/> is the feature
    /// area (<c>"agent"</c>), <paramref name="configKey"/> the specific missing
    /// key (<c>"role:{role}"</c>), <paramref name="scope"/> the owner scope
    /// (<c>"system"</c> for a missing system default). Best-effort — a recorder
    /// failure must never mask the originating <c>TammaError</c>.
    /// </summary>
    Task RecordAsync(
        string domain, string configKey, string scope,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken ct = default);
}
