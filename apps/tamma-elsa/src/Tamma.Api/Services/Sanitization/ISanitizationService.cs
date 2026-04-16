using Tamma.Data.Entities;

namespace Tamma.Api.Services.Sanitization;

/// <summary>
/// Tenant-aware content sanitizer.
/// </summary>
public interface ISanitizationService
{
    /// <summary>
    /// Apply all enabled rules (system defaults merged with the tenant's overrides)
    /// to <paramref name="input"/> in priority order.
    /// </summary>
    /// <param name="input">Text to sanitize. May be empty but should not be null.</param>
    /// <param name="tenantId">
    /// Tenant scope. <c>null</c> means use the system-default rule set unmodified
    /// and look up global overrides stored against the <c>null</c> tenant row.
    /// </param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    Task<SanitizeResult> SanitizeAsync(
        string input,
        Guid? tenantId,
        CancellationToken cancellationToken = default);
}
