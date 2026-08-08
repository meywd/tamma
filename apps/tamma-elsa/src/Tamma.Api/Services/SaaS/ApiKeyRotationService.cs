using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tamma.Api.Services.GitHub;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.SaaS;

/// <summary>
/// Concrete <see cref="IApiKeyRotationService"/>.
///
/// <para>Ported from the deleted TypeScript <c>routes/saas/key-rotation.ts</c>
/// (Epic 19 Phase 3). Audit findings 013 + 018 — re-provisioning to GitHub
/// Actions secrets now flows through <see cref="IGitHubSecretsProvisioner"/>;
/// when the Null impl is wired (default until the GitHub App client port
/// lands) the per-repo summary is populated with
/// <c>github_client_not_configured</c> entries so operators see exactly which
/// repos still hold the stale secret.</para>
/// </summary>
public sealed class ApiKeyRotationService : IApiKeyRotationService
{
    private const string Scope = "installation";
    private const string KeyLabel = "installation-key";
    private const int PrefixLength = 16;
    private static readonly HashSet<string> PrivilegedRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "owner",
        "admin"
    };

    private readonly IInstallationRepository _installations;
    private readonly IApiKeyRepository _apiKeys;
    private readonly ITenantMembershipRepository _memberships;
    private readonly IEventRepository _events;
    private readonly IInstallationSecretsPusher _secretsPusher;
    private readonly ILogger<ApiKeyRotationService> _logger;

    public ApiKeyRotationService(
        IInstallationRepository installations,
        IApiKeyRepository apiKeys,
        ITenantMembershipRepository memberships,
        IEventRepository events,
        IInstallationSecretsPusher secretsPusher,
        ILogger<ApiKeyRotationService> logger)
    {
        _installations = installations;
        _apiKeys = apiKeys;
        _memberships = memberships;
        _events = events;
        _secretsPusher = secretsPusher;
        _logger = logger;
    }

    public Task<KeyRotationResult> RotateAsync(Guid installationEntityId, Guid callerUserId)
        => RotateInternalAsync(() => _installations.GetByEntityIdAsync(installationEntityId), callerUserId);

    public Task<KeyRotationResult> RotateByInstallationIdAsync(long installationId, Guid callerUserId)
        => RotateInternalAsync(() => _installations.GetByInstallationIdAsync(installationId), callerUserId);

    private async Task<KeyRotationResult> RotateInternalAsync(
        Func<Task<GitHubInstallation?>> resolveInstallation, Guid callerUserId)
    {
        var installation = await resolveInstallation();
        if (installation is null)
        {
            _logger.LogWarning(
                "Rotate rejected: installation not found for caller {UserId}",
                callerUserId);
            return Fail("not_found");
        }

        var installationEntityId = installation.Id;

        if (installation.TenantId is null)
        {
            _logger.LogWarning(
                "Rotate rejected: installation {InstallationEntityId} has no tenant",
                installationEntityId);
            return Fail("no_tenant");
        }

        if (installation.SuspendedAt is not null)
        {
            _logger.LogWarning(
                "Rotate rejected: installation {InstallationEntityId} is suspended",
                installationEntityId);
            return Fail("suspended");
        }

        var tenantId = installation.TenantId.Value;
        var role = await _memberships.GetRoleAsync(tenantId, callerUserId);
        if (role is null || !PrivilegedRoles.Contains(role))
        {
            _logger.LogWarning(
                "Rotate rejected: user {UserId} role={Role} not privileged on tenant {TenantId}",
                callerUserId, role, tenantId);
            return Fail("forbidden");
        }

        var plaintext = GenerateKey();
        var keyHash = HashKey(plaintext);
        var keyPrefix = plaintext.Length >= PrefixLength ? plaintext[..PrefixLength] : plaintext;

        ApiKey stored;
        var existing = (await _apiKeys.ListByOwnerAsync(installationEntityId.ToString()))
            .FirstOrDefault(k => string.Equals(k.Scope, Scope, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            stored = await _apiKeys.RotateAsync(existing.Id, keyHash, keyPrefix);
        }
        else
        {
            stored = await _apiKeys.CreateAsync(new ApiKey
            {
                Scope = Scope,
                OwnerId = installationEntityId.ToString(),
                KeyHash = keyHash,
                KeyPrefix = keyPrefix,
                Label = KeyLabel,
                Permissions = Array.Empty<string>(),
                TenantId = tenantId
            });
        }

        await _events.AppendAsync(new DomainEvent
        {
            Type = "API_KEY.ROTATED",
            TenantId = tenantId,
            Tags = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system",
                ["installationEntityId"] = installationEntityId,
                ["installationId"] = installation.InstallationId
            }),
            Metadata = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["eventSource"] = "system",
                ["workflowVersion"] = "1.0.0"
            }),
            Data = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["installationEntityId"] = installationEntityId,
                ["installationId"] = installation.InstallationId,
                ["rotatedByUserId"] = callerUserId,
                ["keyId"] = stored.Id,
                ["keyPrefix"] = keyPrefix,
                ["rotatedFromId"] = existing?.Id
            })
        });

        _logger.LogInformation(
            "API key rotated: installation={InstallationEntityId} tenant={TenantId} user={UserId} newKeyId={KeyId}",
            installationEntityId, tenantId, callerUserId, stored.Id);

        // Audit finding 013 + 021 — TS atomic-rotated the DB hash AND
        // re-provisioned the new plaintext to every repo's GitHub Actions
        // secrets so running workflows never broke. We delegate to the
        // injected provisioner; when the Null impl is wired every entry
        // surfaces `github_client_not_configured` (matches the prior
        // hardcoded summary; once the real provisioner lands the per-repo
        // results turn green automatically without touching this code).
        var repos = await _installations.ListReposAsync(installationEntityId)
            ?? new List<Tamma.Data.Entities.GitHubInstallationRepo>();
        var repoTuples = (IReadOnlyList<(string Owner, string Repo)>)repos
            .Where(r => r.IsActive
                && !string.IsNullOrEmpty(r.Owner)
                && !string.IsNullOrEmpty(r.Name))
            .Select(r => (r.Owner, r.Name))
            .ToList();
        // Epic 31 P4 M4 — re-provisioning rides the driver plane (see
        // DriverInstallationSecretsPusher): the tenant's resolved GitHub
        // driver pushes through ICiSecretsProvisioner; no driver degrades to
        // per-repo github_client_not_configured, the previous behavior.
        var provisionResults = await _secretsPusher.PushAsync(
            tenantId, repoTuples, "TAMMA_API_KEY", plaintext);
        var perRepoResults = provisionResults
            .Select(r => new RepoProvisioningResult(
                Owner: r.Owner,
                Repo: r.Repo,
                Success: r.Success,
                Error: r.Error))
            .ToList();
        var successCount = perRepoResults.Count(r => r.Success);
        var summary = new KeyRotationProvisioningSummary(
            Total: perRepoResults.Count,
            Success: successCount,
            Failed: perRepoResults.Count - successCount,
            Results: perRepoResults);

        return new KeyRotationResult(
            Success: true,
            PlaintextKey: plaintext,
            KeyPrefix: keyPrefix,
            KeyId: stored.Id,
            ErrorReason: null,
            Provisioning: summary);
    }

    private static KeyRotationResult Fail(string reason) =>
        new(Success: false, PlaintextKey: null, KeyPrefix: null, KeyId: null, ErrorReason: reason, Provisioning: null);

    private static string GenerateKey()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        // Base64 produces URL-unsafe characters; swap `/` / `+` so the key is
        // safe for both HTTP headers and visible use in the UI.
        var body = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return $"tamma_sk_{body}";
    }

    private static string HashKey(string plaintext)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();
}
