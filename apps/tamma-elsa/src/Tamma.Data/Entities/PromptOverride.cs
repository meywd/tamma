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
    public int MaxTokens { get; set; } = 4096;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
