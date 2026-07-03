using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Story 30-3 — real, facade-based implementation of
/// <see cref="IProvisioningSecretRegistrar"/> (the RegisterSecrets saga
/// step) built on the Epic 29 <see cref="ISecretStore"/> cabinet.
///
/// <para><b>Secret registered</b>: <c>tenant:cranl/app-env-hmac</c> — a
/// per-tenant HMAC that shadows the platform-wide
/// <c>TAMMA_SHARED_SECRET</c>. Splitting the shared secret per tenant
/// isolates a tenant compromise to that tenant's engine. A fresh
/// cryptographically-random value is minted per tenant (NOT a copy of the
/// platform <c>Tamma:TenantSharedSecret</c>).</para>
///
/// <para><b>Secret deliberately NOT registered</b>:
/// <c>tenant:db/cranl-connection</c> (named in story brief AC9). Post
/// Epic-30 Phase B (commit <c>c44261f7</c>) the stored per-tenant
/// <c>DATABASE_URL</c> model was removed — DB routing flows through the
/// unified pool's AES-GCM connection-string envelope, and
/// <c>CranlTenantProviderV2.TryBuildEndpoints</c> returns
/// <c>DatabaseUrl = string.Empty</c> (reading only the engine host). No
/// consumer reads a <c>tenant:db/cranl-connection</c> secret, so it would
/// be a vestigial write. It is intentionally omitted.</para>
///
/// <para><b>Consumed today?</b> No. The Cranl env-var push
/// (<c>CranlProvisioningWorkflow.BuildEnvironment</c>) still sources
/// <c>TAMMA_SHARED_SECRET</c> from the PLATFORM-scoped
/// <c>hmac/shared-engine</c> secret, not this per-tenant shadow. This
/// registrar lays down the per-tenant row so a future switch (and Epic 29
/// per-tenant rotation) has something to rotate; wiring the env push to
/// read it is a follow-up. Combined with the dormant dedicated-compute
/// Cranl path, this step is unit-testable but not exercised end-to-end in
/// the default deployment.</para>
/// </summary>
public sealed class ProvisioningSecretRegistrar : IProvisioningSecretRegistrar
{
    /// <summary>Cabinet slug for the per-tenant HMAC / TAMMA_SHARED_SECRET
    /// shadow. Scope is <see cref="SecretScope.Tenant"/>; the
    /// <c>tenant:</c> prefix in the brief is the SCOPE, not part of the
    /// slug (which must match the cabinet name regex).</summary>
    public const string HmacSecretName = "cranl/app-env-hmac";

    /// <summary>Byte length of the generated per-tenant HMAC before
    /// base64url encoding. 32 bytes = 256 bits of entropy.</summary>
    private const int HmacByteLength = 32;

    /// <summary>Deterministic system-actor owner for provisioning-minted
    /// secrets. The cabinet requires a non-empty owner GUID; provisioning
    /// runs with no authenticated user, so we use a stable well-known id
    /// (Epic 30 suffix) — mirrors <c>StopgapSecretMigrator</c>'s
    /// deterministic-actor convention (Epic 29 suffix).</summary>
    private static readonly Guid SystemProvisioningActor =
        Guid.Parse("00000000-0000-0000-0000-000000000030");

    // ISecretStore is OPTIONAL: it is only wired on the Postgres cabinet
    // path (AddTammaPostgresSecrets). On a dev/in-memory host it is absent,
    // and that is fine for every non-dedicated topology (a clean guarded
    // no-op). It becomes REQUIRED only when a DedicatedCompute tenant must
    // register the HMAC — that path fails loud (see RegisterInitialSecretsAsync).
    private readonly ISecretStore? _secretStore;
    private readonly ILogger<ProvisioningSecretRegistrar> _logger;

    public ProvisioningSecretRegistrar(
        ISecretStore? secretStore,
        ILogger<ProvisioningSecretRegistrar> logger)
    {
        _secretStore = secretStore;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretRef>> RegisterInitialSecretsAsync(
        Guid tenantId, ProvisioningTopology topology, CancellationToken ct = default)
    {
        // ── GUARDED NO-OP ────────────────────────────────────────────────
        // Only a dedicated per-tenant engine needs a per-tenant
        // TAMMA_SHARED_SECRET. DatabaseOnly / Managed tenants run on shared
        // platform compute — no engine env to sign — so there is nothing to
        // register. Return an empty list: a DELIBERATE, documented no-op that
        // completes the step cleanly (NOT a stub, NOT a deferral).
        if (topology != ProvisioningTopology.DedicatedCompute)
        {
            _logger.LogDebug(
                "RegisterSecrets no-op for tenant {TenantId}: topology {Topology} has no " +
                "per-tenant engine (no TAMMA_SHARED_SECRET shadow required).",
                tenantId, topology);
            return Array.Empty<SecretRef>();
        }

        // ── FAIL LOUD ────────────────────────────────────────────────────
        // Dedicated compute genuinely needs the HMAC. If the cabinet is not
        // configured we refuse — silently provisioning a per-tenant engine
        // with no shared secret would surface later as an un-diagnosable HMAC
        // mismatch on every signed engine call.
        if (_secretStore is null)
        {
            throw new InvalidOperationException(
                "Dedicated-compute provisioning requires the secret cabinet " +
                "(ISecretStore / AddTammaPostgresSecrets), which is not configured. " +
                "Refusing to register the per-tenant TAMMA_SHARED_SECRET shadow.");
        }

        var hmacRef = SecretRef.ForTenant(tenantId, HmacSecretName);

        // Idempotency: a resumed/retried provision (or a re-attempt after a
        // rolled-back one) must not double-create. CreateAsync throws on a
        // duplicate (scope, tenant, name), and the facade has no delete for a
        // sole-active-version secret, so an existing row is treated as
        // already-registered and reused.
        var existing = await _secretStore.GetAsync(hmacRef, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Per-tenant HMAC shadow already present for tenant {TenantId}; reusing " +
                "(idempotent register).", tenantId);
            return new[] { hmacRef };
        }

        var hmacValue = GenerateHmacSecret();

        // CreateAsync mints v1 + activates it (ActiveVersionNumber = 1) and
        // emits SECRET.WRITE. Any failure (duplicate race, fail-closed backend)
        // surfaces to the caller unchanged → the saga step fails loud.
        await _secretStore.CreateAsync(
            new CreateSecretRequest(
                Name: HmacSecretName,
                Scope: SecretScope.Tenant,
                TenantId: tenantId,
                Purpose: SecretPurpose.HmacSharedSecret,
                ConsumerRefs: new[] { new ConsumerRef("cranl", "env=TAMMA_SHARED_SECRET") },
                OwnerUserId: SystemProvisioningActor,
                RotationSchedule: RotationSchedule.None,
                InitialPlaintext: hmacValue),
            ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Registered per-tenant HMAC shadow ({SecretName}) for tenant {TenantId}.",
            HmacSecretName, tenantId);

        return new[] { hmacRef };
    }

    /// <inheritdoc />
    public async Task RetireInitialSecretsAsync(
        IReadOnlyList<SecretRef> registered, CancellationToken ct = default)
    {
        // Nothing could have been registered without a cabinet, and an empty
        // list (guarded no-op path) is a pure no-op. Both branches make
        // compensation safe to run on any rollback.
        if (_secretStore is null || registered is null || registered.Count == 0)
        {
            return;
        }

        foreach (var reference in registered)
        {
            try
            {
                var meta = await _secretStore.GetAsync(reference, ct).ConfigureAwait(false);
                if (meta is null || meta.ActiveVersionNumber <= 0)
                {
                    // Never created (register threw before CreateAsync landed)
                    // or already scrubbed — nothing to retire. Idempotent.
                    continue;
                }

                await _secretStore
                    .RetireVersionAsync(reference, meta.ActiveVersionNumber, ct)
                    .ConfigureAwait(false);
            }
            catch (KeyNotFoundException)
            {
                // Secret / version already gone — idempotent no-op.
            }
            catch (InvalidOperationException ex)
            {
                // The facade refuses to retire a secret's SOLE active version
                // (RetireVersionAsync guards the active pointer, and
                // ISecretStore exposes no row-delete). A freshly-registered v1
                // therefore cannot be fully revoked here. This is acceptable:
                // RegisterInitialSecretsAsync is idempotent (it reuses an
                // existing row), so a re-provision does NOT double-create, and
                // the Story 30-9 reconciliation sweep reclaims the orphaned row.
                // A compensation must never throw — log and continue.
                _logger.LogWarning(ex,
                    "Compensation could not fully retire provisioning secret {SecretKey} " +
                    "(facade has no delete for a sole active version); leaving it for the " +
                    "reconciliation sweep. Re-provision stays safe (register is idempotent).",
                    reference.ToStorageKey());
            }
        }
    }

    /// <summary>Mint a fresh, URL-safe per-tenant HMAC value.</summary>
    private static string GenerateHmacSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(HmacByteLength);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
