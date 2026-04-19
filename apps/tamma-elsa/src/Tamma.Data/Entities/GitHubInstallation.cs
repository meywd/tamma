namespace Tamma.Data.Entities;

public class GitHubInstallation
{
    public Guid Id { get; set; }
    public long InstallationId { get; set; }
    public string AccountLogin { get; set; } = null!;

    /// <summary>
    /// GitHub account type. Constrained to <c>User | Organization</c> via a
    /// CHECK constraint (matches TS migration 001).
    /// </summary>
    public string AccountType { get; set; } = null!;

    /// <summary>
    /// GitHub App ID. Widened to <c>bigint</c> (long) to match TS — App IDs
    /// are bigint in the GitHub API, and narrowing to int32 leaves no
    /// headroom.
    /// </summary>
    public long AppId { get; set; }

    public string? AppSlug { get; set; }
    public string Permissions { get; set; } = "{}";
    public DateTime? SuspendedAt { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<GitHubInstallationRepo> Repos { get; set; } = [];
}
