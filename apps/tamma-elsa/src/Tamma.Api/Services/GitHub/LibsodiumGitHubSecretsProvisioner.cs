using Octokit;
using Sodium;

namespace Tamma.Api.Services.GitHub;

/// <summary>
/// Real <see cref="IGitHubSecretsProvisioner"/> implementation using
/// libsodium sealed-box encryption (<c>crypto_box_seal</c>) + Octokit's
/// Actions secrets API (<c>GET /repos/{owner}/{repo}/actions/secrets/public-key</c>
/// and <c>PUT /repos/{owner}/{repo}/actions/secrets/{name}</c>).
///
/// <para>GitHub Actions secrets use X25519 sealed boxes — the repo public
/// key is base64-encoded, the plaintext is encrypted via libsodium's
/// <c>crypto_box_seal</c> primitive, and the ciphertext is base64-encoded
/// before being PUT back. <see cref="Sodium.SealedPublicKeyBox.Create"/>
/// wraps <c>crypto_box_seal</c> directly.</para>
///
/// <para>Concurrency is capped at 5 parallel writes (matches the TS impl's
/// <c>MAX_CONCURRENCY</c>). Per-repo failures do not throw — each repo
/// gets a <see cref="SecretProvisionResult"/> entry and the batch continues.</para>
///
/// <para>Audit findings: github 013, engine 021.</para>
/// </summary>
public sealed class LibsodiumGitHubSecretsProvisioner : IGitHubSecretsProvisioner
{
    // Matches the TS impl at packages/api/src/services/github-secrets-provisioner.ts:39
    private const int MaxConcurrency = 5;

    private readonly OctokitGitHubAppClient _appClient;
    private readonly ILogger<LibsodiumGitHubSecretsProvisioner> _logger;

    public LibsodiumGitHubSecretsProvisioner(
        OctokitGitHubAppClient appClient,
        ILogger<LibsodiumGitHubSecretsProvisioner> logger)
    {
        _appClient = appClient;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SecretProvisionResult>> ProvisionSecretAsync(
        long installationId,
        IReadOnlyList<(string Owner, string Repo)> repos,
        string secretName,
        string secretValue,
        CancellationToken ct = default)
    {
        if (repos.Count == 0)
        {
            return Array.Empty<SecretProvisionResult>();
        }

        var client = await _appClient.GetInstallationClientAsync(installationId, ct).ConfigureAwait(false);

        var results = new SecretProvisionResult[repos.Count];
        using var gate = new SemaphoreSlim(MaxConcurrency);

        var tasks = new List<Task>(repos.Count);
        for (int i = 0; i < repos.Count; i++)
        {
            var index = i;
            var (owner, repo) = repos[i];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[index] = await WriteSingleSecretAsync(
                        client, owner, repo, secretName, secretValue, ct).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<SecretProvisionResult> WriteSingleSecretAsync(
        IGitHubClient client,
        string owner,
        string repo,
        string secretName,
        string secretValue,
        CancellationToken ct)
    {
        try
        {
            // 1. Fetch the repo's sealed-box public key.
            var publicKey = await client.Repository.Actions.Secrets
                .GetPublicKey(owner, repo)
                .WaitAsync(ct).ConfigureAwait(false);

            // 2. Encrypt plaintext via libsodium sealed-box (ephemeral keypair +
            // X25519 + XSalsa20-Poly1305). crypto_box_seal is anonymous — no
            // recipient identity is attached.
            var encryptedValue = EncryptSealedBox(publicKey.Key, secretValue);

            // 3. PUT the ciphertext.
            await client.Repository.Actions.Secrets
                .CreateOrUpdate(owner, repo, secretName,
                    new UpsertRepositorySecret
                    {
                        EncryptedValue = encryptedValue,
                        KeyId = publicKey.KeyId
                    })
                .WaitAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Provisioned secret {SecretName} to {Owner}/{Repo}", secretName, owner, repo);
            return new SecretProvisionResult(owner, repo, Success: true, Error: null);
        }
        catch (RateLimitExceededException ex)
        {
            _logger.LogWarning(ex,
                "Rate limit exceeded provisioning {SecretName} to {Owner}/{Repo}; resetAt={ResetAt:o}",
                secretName, owner, repo, ex.Reset);
            return new SecretProvisionResult(owner, repo, false, "github_rate_limited");
        }
        catch (AbuseException ex)
        {
            _logger.LogWarning(ex,
                "Abuse detection triggered provisioning {SecretName} to {Owner}/{Repo}",
                secretName, owner, repo);
            return new SecretProvisionResult(owner, repo, false, "github_abuse_detected");
        }
        catch (ApiException ex) when (IsArchivedRepoError(ex))
        {
            _logger.LogInformation(
                "Skipped archived repo {Owner}/{Repo} for secret {SecretName}", owner, repo, secretName);
            return new SecretProvisionResult(owner, repo, false, $"Skipped archived repo {owner}/{repo}");
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning(ex,
                "Forbidden while provisioning {SecretName} to {Owner}/{Repo} — likely permission or archived",
                secretName, owner, repo);
            return new SecretProvisionResult(owner, repo, false,
                $"Failed to provision secret for {owner}/{repo}: {ex.Message}");
        }
        catch (ApiException ex)
        {
            _logger.LogWarning(ex,
                "GitHub API error provisioning {SecretName} to {Owner}/{Repo}: {Status}",
                secretName, owner, repo, (int)ex.StatusCode);
            return new SecretProvisionResult(owner, repo, false,
                $"Failed to provision secret for {owner}/{repo}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error provisioning {SecretName} to {Owner}/{Repo}",
                secretName, owner, repo);
            return new SecretProvisionResult(owner, repo, false,
                $"Failed to provision secret for {owner}/{repo}: {ex.Message}");
        }
    }

    /// <summary>
    /// Sealed-box encrypt <paramref name="plaintext"/> with a standard base64-
    /// encoded X25519 public key (GitHub's wire format). Returns a standard
    /// base64 ciphertext, which GitHub expects in the <c>encrypted_value</c>
    /// field.
    /// </summary>
    internal static string EncryptSealedBox(string publicKeyBase64, string plaintext)
    {
        var publicKey = Convert.FromBase64String(publicKeyBase64);
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var encrypted = SealedPublicKeyBox.Create(messageBytes, publicKey);
        return Convert.ToBase64String(encrypted);
    }

    private static bool IsArchivedRepoError(ApiException ex)
    {
        // GitHub returns 403 with `Repository was archived so is read-only`
        // in the message when the target is archived.
        return ex.Message.Contains("archived", StringComparison.OrdinalIgnoreCase)
            || ex.ApiError?.Message?.Contains("archived", StringComparison.OrdinalIgnoreCase) == true;
    }
}
