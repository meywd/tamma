using Tamma.Data.Entities;

namespace Tamma.Api.Services.Sanitization;

/// <summary>
/// Direction of a sanitisation pass — input bound for an LLM vs output
/// returned from one. Differentiates the pipelines per finding 006: input
/// strips HTML aggressively and runs injection detection, output preserves
/// fenced code blocks and skips heuristic detection.
/// </summary>
public enum SanitizeDirection
{
    /// <summary>User input bound for an LLM. Default.</summary>
    Input,
    /// <summary>Model output returned to a caller / downstream tool.</summary>
    Output,
}

/// <summary>
/// Tenant-aware content sanitizer.
/// </summary>
public interface ISanitizationService
{
    /// <summary>
    /// Apply all enabled rules (system defaults merged with the tenant's overrides)
    /// to <paramref name="input"/> in priority order, then run the
    /// <see cref="IContentSanitizer"/> pipeline appropriate for the
    /// <paramref name="direction"/>.
    /// </summary>
    /// <param name="input">Text to sanitize. May be empty but should not be null.</param>
    /// <param name="tenantId">
    /// Tenant scope. <c>null</c> means use the system-default rule set unmodified
    /// and look up global overrides stored against the <c>null</c> tenant row.
    /// </param>
    /// <param name="direction">Whether this is input or output. Default: input.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<SanitizeResult> SanitizeAsync(
        string input,
        Guid? tenantId,
        SanitizeDirection direction = SanitizeDirection.Input,
        CancellationToken cancellationToken = default);
}
