namespace Tamma.Platforms.GitHub;

/// <summary>
/// Epic 31 P1 stage 2 — discriminated union for GitHub credential
/// shapes, mirroring the <c>GiteaAuth</c> pattern.
///
/// <para>Two variants:</para>
/// <list type="bullet">
///   <item><see cref="Pat"/> — a personal access token / BYOK
///         plaintext bearer. The most common shape: whatever the
///         operator pasted into the onboarding connect flow
///         (<c>PlatformConnectService</c> stores the plaintext
///         verbatim in the secret cabinet; the driver defines the
///         parse).</item>
///   <item><see cref="App"/> — GitHub App installation mode. The
///         driver mints short-lived installation access tokens from
///         the App's RS256 private key (the logic absorbed from
///         <c>Tamma.Api</c>'s <c>OctokitGitHubAppClient</c>), fixing
///         the old process-singleton App-only conditional: the key now
///         arrives per-installation through the factory seam.</item>
/// </list>
///
/// <para><b>Wire format</b> (parsed by <see cref="Parse"/>):</para>
/// <list type="bullet">
///   <item>Raw non-JSON string → <see cref="Pat"/>. Backwards
///         compatible with tokens operators paste in
///         (<c>ghp_…</c>/<c>github_pat_…</c>).</item>
///   <item>JSON <c>{ "kind": "pat", "token": "…" }</c> →
///         <see cref="Pat"/> (defensive JSON wrapper).</item>
///   <item>JSON <c>{ "kind": "app", "appId": 123,
///         "privateKeyPem": "-----BEGIN…", "installationId": 456 }</c>
///         → <see cref="App"/>. <c>installationId</c> is optional in
///         the credential itself — when omitted the factory falls back
///         to the installation row's
///         <c>PlatformInstallation.InstallationExternalId</c> (that is
///         where the P2 registry bridge records the GitHub
///         installation id).</item>
/// </list>
///
/// <para>Plaintext lives only on the call stack between
/// <see cref="GitHubPlatformDriverFactory.CreateAsync"/> and the
/// <see cref="GitHubHttpClient"/> constructor. Parse throws on
/// malformed input — bad credentials should fail at the factory, not
/// at first use.</para>
/// </summary>
public abstract record GitHubAuth
{
    private GitHubAuth() { }

    /// <summary>
    /// Static bearer token (PAT / fine-grained PAT / BYOK token) —
    /// sent as <c>Authorization: Bearer &lt;token&gt;</c>.
    /// </summary>
    public sealed record Pat(string Token) : GitHubAuth
    {
        public override string ToString() => "Pat(****)";
    }

    /// <summary>
    /// GitHub App installation credential. The driver builds a
    /// 10-minute RS256 App JWT from <see cref="PrivateKeyPem"/> and
    /// exchanges it for a ~1h installation access token via
    /// <c>POST /app/installations/{id}/access_tokens</c>, cached and
    /// re-minted on expiry / 401.
    /// </summary>
    /// <param name="AppId">The GitHub App's numeric id (JWT issuer).</param>
    /// <param name="PrivateKeyPem">The App's RSA private key, PEM
    /// (PKCS#1 or PKCS#8).</param>
    /// <param name="InstallationId">The installation to mint tokens
    /// for; null when the credential defers to the installation row's
    /// external id.</param>
    public sealed record App(
        long AppId,
        string PrivateKeyPem,
        long? InstallationId) : GitHubAuth
    {
        public override string ToString() => $"App({AppId}, ****)";
    }

    /// <summary>
    /// Parse a credential plaintext string into the appropriate
    /// variant. See the type-level remarks for the recognized wire
    /// shapes. Throws <see cref="ArgumentException"/> on unparseable
    /// input.
    /// </summary>
    public static GitHubAuth Parse(string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintext);
        var trimmed = plaintext.Trim();

        if (!trimmed.StartsWith('{'))
        {
            // Raw token string — the PAT/BYOK shape.
            return new Pat(trimmed);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;

            if (string.Equals(kind, "pat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "token", StringComparison.OrdinalIgnoreCase))
            {
                var token = root.TryGetProperty("token", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(token))
                {
                    throw new ArgumentException("GitHub pat credential missing 'token'");
                }
                return new Pat(token);
            }

            if (string.Equals(kind, "app", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("appId", out var appIdEl)
                    || !TryReadLong(appIdEl, out var appId)
                    || appId <= 0)
                {
                    throw new ArgumentException(
                        "GitHub app credential requires a positive numeric 'appId'");
                }
                var pem = root.TryGetProperty("privateKeyPem", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(pem))
                {
                    throw new ArgumentException("GitHub app credential missing 'privateKeyPem'");
                }
                long? installationId = null;
                if (root.TryGetProperty("installationId", out var instEl)
                    && TryReadLong(instEl, out var inst))
                {
                    installationId = inst;
                }
                return new App(appId, pem, installationId);
            }

            throw new ArgumentException(
                $"unrecognized GitHub credential kind: '{kind ?? "<missing>"}'");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ArgumentException(
                "GitHub credential plaintext began with '{' but is not valid JSON",
                ex);
        }
    }

    private static bool TryReadLong(System.Text.Json.JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => element.TryGetInt64(out value),
            System.Text.Json.JsonValueKind.String =>
                long.TryParse(element.GetString(), out value),
            _ => false,
        };
    }
}
