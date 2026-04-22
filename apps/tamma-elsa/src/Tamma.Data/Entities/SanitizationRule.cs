namespace Tamma.Data.Entities;

public class SanitizationRule
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>
    /// JSONB blob of sanitization rules. Schema is defined in
    /// <see cref="SanitizationRuleDefinition"/> and serialized as a JSON
    /// array. The C# port keeps the flattened JSONB shape (vs TS migration
    /// 016's six typed columns) but enforces uniqueness and FK CASCADE on
    /// <see cref="TenantId"/> via the Phase-1 hardening migration.
    /// </summary>
    public string Rules { get; set; } = "{}";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Tenant? Tenant { get; set; }
}
