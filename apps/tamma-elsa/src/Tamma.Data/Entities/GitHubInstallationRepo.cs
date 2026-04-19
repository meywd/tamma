namespace Tamma.Data.Entities;

public class GitHubInstallationRepo
{
    public Guid Id { get; set; }
    public Guid InstallationEntityId { get; set; }
    public long RepoId { get; set; }

    /// <summary>
    /// Repo owner ("acme-corp" in "acme-corp/my-repo"). Restored from TS
    /// migration 001 to support per-owner listings without parsing
    /// <see cref="RepoFullName"/> in app code.
    /// </summary>
    public string Owner { get; set; } = null!;

    /// <summary>
    /// Repo short name ("my-repo" in "acme-corp/my-repo"). Restored from TS.
    /// </summary>
    public string Name { get; set; } = null!;

    public string RepoFullName { get; set; } = null!;
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public GitHubInstallation Installation { get; set; } = null!;
}
