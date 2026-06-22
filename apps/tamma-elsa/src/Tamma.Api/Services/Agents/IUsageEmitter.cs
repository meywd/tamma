namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 — MINIMAL interim seam for the 32-9 usage/cost emitter (NOT yet
/// landed). <c>ManagedAgent</c> (T3) emits one usage record per metered run so
/// 35 (billing) / 36 (analytics) have a consumer hook the moment 32-9 ships.
/// Until then the <see cref="NullUsageEmitter"/> default is a no-op — the run
/// still produces its <c>AGENT.RUN.*</c> DCB events (the durable audit trail),
/// so no usage data is lost in the interim; 32-9 will project the usage record
/// from the same fields.
///
/// <para>KEY SAFETY: the record carries the <see cref="UsageRecord.CredentialSource"/>
/// LABEL only — never the provider API key.</para>
/// </summary>
public interface IUsageEmitter
{
    /// <summary>Emit one usage record for a metered managed run. Best-effort —
    /// a failure here must NEVER convert a returned <see cref="AgentRunResult"/>
    /// into a lost run (AC10); the caller logs and swallows.</summary>
    Task EmitAsync(UsageRecord record, CancellationToken ct = default);
}

/// <summary>
/// Story 32-5 — the usage record a metered run produces (consumed by 32-9 /
/// 35 / 36). <see cref="ProviderCostUsd"/> is the raw 34-11 basis;
/// <see cref="PriceUsd"/> is the 34-5 billed amount (0 on the BYOK leg);
/// <see cref="CredentialSource"/> is the <c>"byok"</c>/<c>"platform"</c> label
/// billing branches on. Key-free.
/// </summary>
public sealed record UsageRecord
{
    /// <summary>Tenant scope; null ⇒ single-user / platform.</summary>
    public Guid? TenantId { get; init; }

    /// <summary>Resolved agent identity (null on the legacy path).</summary>
    public Guid? AgentId { get; init; }

    /// <summary>Provider that served the run.</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>Model used.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The role served.</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>Prompt/input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Completion/output tokens consumed.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Raw provider cost basis (34-11). Identical across both
    /// credential legs.</summary>
    public decimal ProviderCostUsd { get; init; }

    /// <summary>Billed price (34-5 markup on platform; 0 on BYOK).</summary>
    public decimal PriceUsd { get; init; }

    /// <summary>Billing mode tag — <c>"byok"</c> | <c>"platform"</c> | null.
    /// NEVER the key.</summary>
    public string? CredentialSource { get; init; }

    /// <summary>Workflow instance id.</summary>
    public string CorrelationId { get; init; } = string.Empty;
}

/// <summary>
/// Story 32-5 — the no-op default until 32-9 lands. Records nothing; the
/// durable usage signal lives in the <c>AGENT.RUN.SUCCESS</c> DCB event until
/// the real emitter ships.
/// </summary>
public sealed class NullUsageEmitter : IUsageEmitter
{
    /// <inheritdoc />
    public Task EmitAsync(UsageRecord record, CancellationToken ct = default) => Task.CompletedTask;
}
