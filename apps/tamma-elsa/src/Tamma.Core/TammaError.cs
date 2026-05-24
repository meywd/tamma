namespace Tamma.Core;

/// <summary>
/// Severity classification for a <see cref="TammaError"/>.
/// </summary>
public enum TammaErrorSeverity
{
    Low,
    Medium,
    High,
    Critical,
}

/// <summary>
/// Structured domain error for the Tamma platform — the C# analogue of the
/// TypeScript-era <c>TammaError</c> class (see <c>CLAUDE.md</c> "Error
/// Handling"). Carries a stable machine-readable <see cref="Code"/>, structured
/// <see cref="Context"/>, a <see cref="Retryable"/> hint, and a
/// <see cref="Severity"/> so failures fail LOUD with diagnostic context instead
/// of degrading silently.
///
/// <para>
/// Introduced by Story 27-18 to give prompt resolution a distinct, identifiable
/// fail-loud exception: a taxonomy-valid <c>(role, action)</c> with no tenant
/// override and no system default is a hard error, never a silent empty/plain
/// fallback (SPEC §7 — "<c>(tenant, role, action)</c> … else <c>TammaError</c>
/// (no silent empty for a taxonomy-valid pair)").
/// </para>
/// </summary>
public sealed class TammaError : Exception
{
    /// <summary>Stable machine-readable error code (e.g. <c>PROMPT.RESOLVE.NO_DEFAULT</c>).</summary>
    public string Code { get; }

    /// <summary>Structured diagnostic context for logs / event payloads.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; }

    /// <summary>Whether retrying the operation could plausibly succeed.</summary>
    public bool Retryable { get; }

    /// <summary>Severity classification.</summary>
    public TammaErrorSeverity Severity { get; }

    public TammaError(
        string code,
        string message,
        IReadOnlyDictionary<string, object?>? context = null,
        bool retryable = false,
        TammaErrorSeverity severity = TammaErrorSeverity.Medium)
        : base(message)
    {
        Code = code;
        Context = context ?? new Dictionary<string, object?>();
        Retryable = retryable;
        Severity = severity;
    }
}
