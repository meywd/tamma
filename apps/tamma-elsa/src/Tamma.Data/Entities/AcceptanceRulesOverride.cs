namespace Tamma.Data.Entities;

/// <summary>
/// A stored acceptance-rules override row (Story 39-5 Design Decision D3),
/// mirroring <see cref="PromptOverride"/>'s dual-scoping shape: single-user rows
/// are keyed on <see cref="UserId"/> (tenant_id NULL), SaaS rows on
/// <see cref="TenantId"/> (user_id NULL); the <c>principal_xor</c> CHECK enforces
/// exactly-one. <see cref="DocumentTypeKey"/> NULL marks the PRINCIPAL BASE row
/// (the deployment-wide dial); a non-null key marks a per-type override. The
/// rules body is one <c>jsonb</c> column (<see cref="RulesJson"/>).
/// </summary>
public class AcceptanceRulesOverride
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? TenantId { get; set; }

    /// <summary>Document-type wire key; NULL = the principal base row (the dial).</summary>
    public string? DocumentTypeKey { get; set; }

    /// <summary>The complete, validated <c>AcceptanceRules</c> body serialized via <c>AcceptanceRulesJson</c>.</summary>
    public string RulesJson { get; set; } = null!;

    /// <summary>Optimistic-concurrency / audit version; bumped on every upsert.</summary>
    public int Version { get; set; } = 1;

    /// <summary>User id that originally created the override.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>User id of the most recent updater.</summary>
    public Guid? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
