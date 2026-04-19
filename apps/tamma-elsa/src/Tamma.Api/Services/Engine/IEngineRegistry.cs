namespace Tamma.Api.Services.Engine;

/// <summary>
/// Lightweight info record for a registered engine. Mirrors the deleted TS
/// <c>EngineInfo</c> shape: <c>{id, state, stats}</c>.
/// </summary>
/// <param name="Id">Stable engine identifier (per-repo or per-deployment).</param>
/// <param name="State">Current engine state (e.g. <c>idle</c>, <c>running</c>).</param>
/// <param name="Stats">Per-engine event/issue counters.</param>
/// <param name="TenantId">Owning tenant; null for self-hosted/system engines.</param>
public sealed record EngineInfo(
    string Id,
    string State,
    EngineStats Stats,
    Guid? TenantId);

/// <summary>Aggregate event counters for a single engine.</summary>
public sealed record EngineStats(int TotalEvents, DateTime? LastEventAt);

/// <summary>
/// Engine registry — the seam between the HTTP layer and the engine
/// process model.
///
/// <para>Audit finding 013 (P1): the deleted TS <c>EngineRegistry</c> kept a
/// named map of <c>TammaEngine</c> instances so the dashboard could
/// enumerate them and clients could route commands by id. The C# port
/// skipped this abstraction entirely. This minimal interface (no real
/// engine implementation yet — that depends on porting <c>TammaEngine</c>,
/// finding 012) lets the dashboard <c>/engines</c> endpoint stop returning
/// hard-coded <c>[]</c> and enumerate whatever engines DI has bound.</para>
///
/// <para>TODO(epic-10/story-10-1): real <c>TammaEngine</c> impl + dynamic
/// register/dispose lifecycle.</para>
/// </summary>
public interface IEngineRegistry
{
    /// <summary>Snapshot the current engine list, optionally filtered by tenant.</summary>
    Task<IReadOnlyList<EngineInfo>> ListAsync(Guid? tenantId, CancellationToken ct = default);

    /// <summary>Process-wide engine count (no tenant filter). Drives dashboard summary.</summary>
    int Count { get; }
}
