namespace Tamma.Data.Entities;

public class GitHubInstallationRepo
{
    public Guid Id { get; set; }
    public Guid InstallationEntityId { get; set; }
    public long RepoId { get; set; }
    public string RepoFullName { get; set; } = null!;
    public bool IsActive { get; set; } = true;

    public GitHubInstallation Installation { get; set; } = null!;
}
