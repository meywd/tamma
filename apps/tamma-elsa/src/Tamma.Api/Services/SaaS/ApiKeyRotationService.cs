using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.SaaS;

/// <summary>
/// Concrete <see cref="IApiKeyRotationService"/>.
///
/// Ported from the deleted TypeScript <c>routes/saas/key-rotation.ts</c>
/// (Epic 19 Phase 3). The TS version also re-provisioned the rotated key to
/// GitHub-hosted repo secrets via Octokit; that re-provisioning is out of
/// scope for this C# port because we do not yet wire a GitHub App client.
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
    private readonly ILogger<ApiKeyRotationService> _logger;

    public ApiKeyRotationService(
        IInstallationRepository installations,
        IApiKeyRepository apiKeys,
        ITenantMembershipRepository memberships,
        IEventRepository events,
        ILogger<ApiKeyRotationService> logger)
    {
        _installations = installations;
        _apiKeys = apiKeys;
        _memberships = memberships;
        _events = events;
        _logger = logger;
    }

    public async Task<KeyRotationResult> RotateAsync(Guid installationEntityId, Guid callerUserId)
    {
        var installation = await _installations.GetByEntityIdAsync(installationEntityId);
        if (installation is null)
        {
            _logger.LogWarning(
                "Rotate rejected: unknown installation entity {InstallationEntityId}",
                installationEntityId);
            return Fail("not_found");
        }

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

        return new KeyRotationResult(
            Success: true,
            PlaintextKey: plaintext,
            KeyPrefix: keyPrefix,
            KeyId: stored.Id,
            ErrorReason: null);
    }

    private static KeyRotationResult Fail(string reason) =>
        new(Success: false, PlaintextKey: null, KeyPrefix: null, KeyId: null, ErrorReason: reason);

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
