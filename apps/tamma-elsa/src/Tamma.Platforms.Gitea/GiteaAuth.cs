namespace Tamma.Platforms.Gitea;

/// <summary>
/// Discriminated union for Gitea credential shapes.
///
/// <para>Two variants surfaced today (impl-plan §locked-decisions):</para>
/// <list type="bullet">
///   <item><see cref="BotToken"/> — single API token (PAT or
///         bot-account access token). The most common deployment shape
///         and the only one the brief commits to for v1.</item>
///   <item><see cref="OAuth2"/> — OAuth2 application with refresh
///         token. Plan §2 calls for refresh-on-401 retry logic; this
///         variant carries the refresh token + client credentials so
///         <see cref="GiteaOAuth2TokenCache"/> can mint short-lived
///         access tokens.</item>
/// </list>
///
/// <para>Plaintext lives only on the call stack between
/// <see cref="GiteaPlatformDriverFactory.CreateAsync"/> and the
/// <see cref="GiteaHttpClient"/> constructor — the credential reader
/// hands a single string and we parse it here.</para>
///
/// <para>Wire format for OAuth2 (parsed by
/// <see cref="GiteaAuth.Parse(string)"/>): JSON
/// <c>{ "kind": "oauth2", "clientId": "...", "clientSecret": "...",
/// "refreshToken": "..." }</c>. For bot tokens the plaintext is the
/// raw token string (no JSON wrapper) — backwards compatible with
/// PAT-style credentials operators paste in.</para>
/// </summary>
public abstract record GiteaAuth
{
    private GiteaAuth() { }

    /// <summary>
    /// Static API token — used as <c>Authorization: token &lt;value&gt;</c>.
    /// Gitea accepts both <c>token</c> and <c>Bearer</c> schemes for
    /// PATs; we standardize on <c>token</c> per Gitea docs.
    /// </summary>
    public sealed record BotToken(string Token) : GiteaAuth
    {
        public override string ToString() => "BotToken(****)";
    }

    /// <summary>
    /// OAuth2 application credential. The driver mints an access token
    /// via <c>POST /login/oauth/access_token</c> with
    /// <c>grant_type=refresh_token</c> on first use and on every 401
    /// retry.
    /// </summary>
    public sealed record OAuth2(
        string ClientId,
        string ClientSecret,
        string RefreshToken) : GiteaAuth
    {
        public override string ToString() => "OAuth2(****)";
    }

    /// <summary>
    /// Parse a credential plaintext string into the appropriate
    /// variant. Recognized shapes:
    /// <list type="bullet">
    ///   <item>Raw token string (most common) → <see cref="BotToken"/>.</item>
    ///   <item>JSON object with <c>kind: "oauth2"</c> → <see cref="OAuth2"/>.</item>
    ///   <item>JSON object with <c>kind: "bot"</c> + <c>token</c> →
    ///         <see cref="BotToken"/> (defensive — accepts JSON-wrapped
    ///         bot tokens too).</item>
    /// </list>
    /// Throws on unparseable input — bad credentials should fail at the
    /// factory, not at first use.
    /// </summary>
    public static GiteaAuth Parse(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        var trimmed = plaintext.Trim();

        // Heuristic: a credential starting with '{' is JSON.
        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
                if (string.Equals(kind, "oauth2", StringComparison.OrdinalIgnoreCase))
                {
                    var clientId = root.GetProperty("clientId").GetString()
                        ?? throw new ArgumentException("oauth2 credential missing clientId");
                    var clientSecret = root.GetProperty("clientSecret").GetString()
                        ?? throw new ArgumentException("oauth2 credential missing clientSecret");
                    var refresh = root.GetProperty("refreshToken").GetString()
                        ?? throw new ArgumentException("oauth2 credential missing refreshToken");
                    return new OAuth2(clientId, clientSecret, refresh);
                }
                if (string.Equals(kind, "bot", StringComparison.OrdinalIgnoreCase))
                {
                    var token = root.GetProperty("token").GetString()
                        ?? throw new ArgumentException("bot credential missing token");
                    return new BotToken(token);
                }
                throw new ArgumentException(
                    $"unrecognized Gitea credential kind: '{kind ?? "<missing>"}'");
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new ArgumentException(
                    "Gitea credential plaintext began with '{' but is not valid JSON",
                    ex);
            }
        }

        // Raw token string.
        return new BotToken(trimmed);
    }
}
