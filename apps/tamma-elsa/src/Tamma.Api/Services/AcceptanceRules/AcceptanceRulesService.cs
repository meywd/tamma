using Tamma.Core;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.AcceptanceRules;

/// <summary>
/// EF-backed <see cref="IAcceptanceRulesResolver"/> (Story 39-5 step 8, Design
/// Decision D1/D2). Implements the three-tier, wholesale-row resolution per mode:
/// <list type="number">
///   <item>principal's per-type override row (documentTypeKey = key)</item>
///   <item>principal's base override row (documentTypeKey NULL — the dial)</item>
///   <item><see cref="AcceptanceDefaults.For"/> — the shipped static default</item>
/// </list>
/// mirroring <c>PromptStoreService</c>'s layer-walk + <c>ForTenant</c> split. Each
/// stored row holds a COMPLETE, validated <see cref="AcceptanceRules"/> body;
/// resolution picks the highest-precedence row wholesale (no field merge). Bodies
/// are validated defensively on read (a corrupt row throws <see cref="TammaError"/>)
/// and fail-loud on write. Mutations emit DCB events best-effort (D12).
/// </summary>
public sealed class AcceptanceRulesService : IAcceptanceRulesResolver
{
    private readonly IAcceptanceRulesRepository _repository;
    private readonly AcceptanceRulesEventsService? _events;
    private readonly TimeProvider _time;

    public AcceptanceRulesService(
        IAcceptanceRulesRepository repository,
        AcceptanceRulesEventsService events,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _events = events;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Test seam — construct without an events service.</summary>
    internal AcceptanceRulesService(IAcceptanceRulesRepository repository, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _events = null;
        _time = timeProvider ?? TimeProvider.System;
    }

    // -----------------------------------------------------------------------
    // Resolution (IAcceptanceRulesResolver)
    // -----------------------------------------------------------------------

    public async Task<ResolvedAcceptanceRules> ResolveAsync(
        Guid? userId, DocumentTypeKey documentType, CancellationToken ct = default)
    {
        var key = documentType.ToWire();

        var typeOverride = await _repository.GetAsync(userId, key);
        if (typeOverride is not null)
            return Materialize(typeOverride, AcceptanceRulesSource.TypeOverride, key);

        var baseOverride = await _repository.GetAsync(userId, null);
        if (baseOverride is not null)
            return Materialize(baseOverride, AcceptanceRulesSource.PrincipalDefault, key);

        return SystemDefault(documentType, key);
    }

    public async Task<ResolvedAcceptanceRules> ResolveForTenantAsync(
        Guid tenantId, DocumentTypeKey documentType, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var key = documentType.ToWire();

        var typeOverride = await _repository.GetByTenantAsync(tenantId, key);
        if (typeOverride is not null)
            return Materialize(typeOverride, AcceptanceRulesSource.TypeOverride, key);

        var baseOverride = await _repository.GetByTenantAsync(tenantId, null);
        if (baseOverride is not null)
            return Materialize(baseOverride, AcceptanceRulesSource.PrincipalDefault, key);

        return SystemDefault(documentType, key);
    }

    /// <summary>
    /// Resolve the PRINCIPAL BASE row (the deployment-wide dial): base override
    /// if present, else the shipped <see cref="AcceptanceDefaults.Rules"/>. Serves
    /// the literal <c>base</c> path segment.
    /// </summary>
    public async Task<ResolvedAcceptanceRules> ResolveBaseAsync(Guid? userId, CancellationToken ct = default)
    {
        var baseOverride = await _repository.GetAsync(userId, null);
        if (baseOverride is not null)
            return Materialize(baseOverride, AcceptanceRulesSource.PrincipalDefault, BaseRowKeyLiteral);
        return BaseSystemDefault();
    }

    /// <summary>Tenant variant of <see cref="ResolveBaseAsync"/>.</summary>
    public async Task<ResolvedAcceptanceRules> ResolveBaseForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var baseOverride = await _repository.GetByTenantAsync(tenantId, null);
        if (baseOverride is not null)
            return Materialize(baseOverride, AcceptanceRulesSource.PrincipalDefault, BaseRowKeyLiteral);
        return BaseSystemDefault();
    }

    private ResolvedAcceptanceRules BaseSystemDefault() =>
        new(
            Rules: AcceptanceDefaults.Rules,
            Source: AcceptanceRulesSource.SystemDefault,
            Version: 1,
            DocumentTypeKey: BaseRowKeyLiteral,
            ResolvedAt: _time.GetUtcNow());

    // -----------------------------------------------------------------------
    // Listing (resolved-with-provenance for every document type)
    // -----------------------------------------------------------------------

    /// <summary>Resolve all 10 document types for a single-user principal (list endpoint).</summary>
    public async Task<IReadOnlyList<ResolvedAcceptanceRules>> ListEffectiveAsync(Guid? userId, CancellationToken ct = default)
    {
        var list = new List<ResolvedAcceptanceRules>();
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            list.Add(await ResolveAsync(userId, type, ct));
        return list;
    }

    /// <summary>Resolve all 10 document types for a SaaS tenant (list endpoint).</summary>
    public async Task<IReadOnlyList<ResolvedAcceptanceRules>> ListEffectiveForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        var list = new List<ResolvedAcceptanceRules>();
        foreach (var type in Enum.GetValues<DocumentTypeKey>())
            list.Add(await ResolveForTenantAsync(tenantId, type, ct));
        return list;
    }

    // -----------------------------------------------------------------------
    // Mutations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Upsert a single-user override. <paramref name="documentTypeKey"/> null (or
    /// the literal <c>base</c>, resolved by the endpoint) addresses the base row.
    /// The rules are validated and the type key rejected fail-loud BEFORE any
    /// repository touch (AC4).
    /// </summary>
    public async Task<(AcceptanceRulesOverride Entity, bool WasCreated)> UpsertAsync(
        Guid? userId, string? documentTypeKey, Tamma.Core.Documents.Policy.AcceptanceRules rules)
    {
        var key = NormalizeAndValidateKey(documentTypeKey);
        var validated = rules.Validate();

        var (saved, wasCreated) = await _repository.UpsertAsync(new AcceptanceRulesOverride
        {
            UserId = userId,
            TenantId = null,
            DocumentTypeKey = key,
            RulesJson = AcceptanceRulesJson.Serialize(validated),
        }, userId);

        await EmitMutationAsync(null, userId, saved, wasCreated, validated.AutonomyLevel);
        return (saved, wasCreated);
    }

    /// <summary>Upsert a tenant-scoped override (SaaS). <paramref name="documentTypeKey"/> null = base row.</summary>
    public async Task<(AcceptanceRulesOverride Entity, bool WasCreated)> UpsertForTenantAsync(
        Guid tenantId, Guid? actingUserId, string? documentTypeKey, Tamma.Core.Documents.Policy.AcceptanceRules rules)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));

        var key = NormalizeAndValidateKey(documentTypeKey);
        var validated = rules.Validate();

        var (saved, wasCreated) = await _repository.UpsertAsync(new AcceptanceRulesOverride
        {
            UserId = null,
            TenantId = tenantId,
            DocumentTypeKey = key,
            RulesJson = AcceptanceRulesJson.Serialize(validated),
        }, actingUserId);

        await EmitMutationAsync(tenantId, actingUserId, saved, wasCreated, validated.AutonomyLevel);
        return (saved, wasCreated);
    }

    /// <summary>Delete a single-user override → fall back to the next tier. Emits RESET on success.</summary>
    public async Task<bool> DeleteAsync(Guid? userId, string? documentTypeKey)
    {
        var key = NormalizeAndValidateKey(documentTypeKey);
        var deleted = await _repository.DeleteAsync(userId, key);
        if (deleted && _events is not null)
            await _events.EmitResetAsync(null, userId, key ?? "base");
        return deleted;
    }

    /// <summary>Delete a tenant-scoped override → fall back to the next tier. Emits RESET on success.</summary>
    public async Task<bool> DeleteForTenantAsync(Guid tenantId, string? documentTypeKey)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant id required.", nameof(tenantId));
        var key = NormalizeAndValidateKey(documentTypeKey);
        var deleted = await _repository.DeleteByTenantAsync(tenantId, key);
        if (deleted && _events is not null)
            await _events.EmitResetAsync(tenantId, null, key ?? "base");
        return deleted;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>The literal path segment addressing the principal base row.</summary>
    public const string BaseRowKeyLiteral = "base";

    /// <summary>
    /// Normalize a raw key: null or the literal <c>base</c> → null (the base row);
    /// otherwise parse it against the 39-2 registry (rejects a typo fail-loud, AC4).
    /// </summary>
    private static string? NormalizeAndValidateKey(string? raw)
    {
        if (raw is null) return null;
        if (string.Equals(raw, BaseRowKeyLiteral, StringComparison.Ordinal)) return null;
        // Throws TammaError DOCUMENT.TYPE.UNKNOWN on an unknown key.
        return DocumentTypeKeyExtensions.Parse(raw).ToWire();
    }

    private ResolvedAcceptanceRules Materialize(AcceptanceRulesOverride row, AcceptanceRulesSource source, string key)
    {
        // Defensive validation on read (D3) — a corrupt row throws, never degrades.
        var rules = AcceptanceRulesJson.Deserialize(row.RulesJson);
        return new ResolvedAcceptanceRules(
            Rules: rules,
            Source: source,
            Version: row.Version,
            DocumentTypeKey: key,
            ResolvedAt: _time.GetUtcNow());
    }

    private ResolvedAcceptanceRules SystemDefault(DocumentTypeKey type, string key) =>
        new(
            Rules: AcceptanceDefaults.For(type),
            Source: AcceptanceRulesSource.SystemDefault,
            Version: 1,
            DocumentTypeKey: key,
            ResolvedAt: _time.GetUtcNow());

    private async Task EmitMutationAsync(
        Guid? tenantId, Guid? actingUserId, AcceptanceRulesOverride saved, bool wasCreated, int autonomyLevel)
    {
        if (_events is null) return;
        var key = saved.DocumentTypeKey ?? "base";
        if (wasCreated)
            await _events.EmitCreatedAsync(tenantId, actingUserId, key, autonomyLevel, saved.Version);
        else
            await _events.EmitUpdatedAsync(tenantId, actingUserId, key, autonomyLevel, saved.Version);
    }
}
