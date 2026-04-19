namespace Tamma.Data.Entities;

public class PromptOverride
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }
    public string Scope { get; set; } = null!;
    public string? Role { get; set; }
    public string? Action { get; set; }
    public string Template { get; set; } = null!;
    public string? SystemPrompt { get; set; }
    public string[] Variables { get; set; } = [];
    public bool EnableTools { get; set; }

    /// <summary>
    /// Maximum tokens. Constrained by a CHECK to be > 0; default 4096.
    /// </summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Optimistic-concurrency token. Restored from TS migration 012 to
    /// detect concurrent edits to the same (user, scope, role, action) row.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>User id that originally created the override.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>User id of the most recent updater.</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
