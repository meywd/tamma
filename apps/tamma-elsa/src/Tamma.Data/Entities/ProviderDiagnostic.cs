namespace Tamma.Data.Entities;

public class ProviderDiagnostic
{
    public Guid Id { get; set; }
    public string ProviderKey { get; set; } = null!;
    public double RequestDurationMs { get; set; }
    public int TokensUsed { get; set; }
    public decimal Cost { get; set; }
    public Guid? TenantId { get; set; }
    public string? Model { get; set; }
    public string? RequestType { get; set; }
    public bool Success { get; set; } = true;
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
}
