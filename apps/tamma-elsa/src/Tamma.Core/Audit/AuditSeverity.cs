namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-1 — coarse severity carried on every curated audit record so a
/// compliance feed can triage ("show me every <c>critical</c> action last
/// quarter") without re-deriving from the action code. Stored on
/// <c>audit_records.severity</c> as the member name (lowercased).
/// </summary>
public enum AuditSeverity
{
    /// <summary>Routine, expected activity (e.g. login success).</summary>
    Info,

    /// <summary>Noteworthy but not alarming (e.g. config / persona edits).</summary>
    Notice,

    /// <summary>Privilege- or money-affecting (e.g. role change, plan change).</summary>
    Warning,

    /// <summary>Highest-sensitivity (e.g. secret reveal, impersonation).</summary>
    Critical,
}
