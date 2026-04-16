namespace Tamma.Data.Entities;

public class GitHubInstallation
{
    public Guid Id { get; set; }
    public long InstallationId { get; set; }
    public string AccountLogin { get; set; } = null!;
    public string AccountType { get; set; } = null!;
    public int AppId { get; set; }
    public string? AppSlug { get; set; }
    public string Permissions { get; set; } = "{}";
    public DateTime? SuspendedAt { get; set; }
    public Guid? TenantId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ICollection<GitHubInstallationRepo> Repos { get; set; } = [];
}
