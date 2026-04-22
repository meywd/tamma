using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Api.Auth;

/// <summary>
/// Encodes/decodes the OAuth <c>state</c> query parameter used by the GitHub
/// authorize → callback round-trip. Mirrors TS
/// <c>packages/api/src/routes/auth/github-oauth.ts:69-86</c>.
///
/// <para>State payload (JSON): <c>{ rd?: string, invite?: string, csrf: string }</c>
/// — <c>rd</c> is a sanitized post-login redirect URL (Tamma origins only),
/// <c>invite</c> is a raw invite token to be hashed and looked up on callback,
/// <c>csrf</c> is a 32-byte random nonce that must round-trip via the
/// short-lived <c>tamma_oauth_csrf</c> cookie.</para>
///
/// <para>Encoding: base64url(JSON UTF-8). No HMAC — the CSRF cookie binding
/// provides integrity (an attacker can fabricate a state but cannot forge a
/// matching cookie under SameSite=Strict).</para>
/// </summary>
public sealed record OAuthStatePayload(
    [property: JsonPropertyName("rd")] string? Rd,
    [property: JsonPropertyName("invite")] string? Invite,
    [property: JsonPropertyName("csrf")] string Csrf);

public static class OAuthStateCodec
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Encode(OAuthStatePayload payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        return Base64Url.Encode(Encoding.UTF8.GetBytes(json));
    }

    public static OAuthStatePayload? TryDecode(string state)
    {
        if (string.IsNullOrEmpty(state)) return null;
        try
        {
            var bytes = Base64Url.Decode(state);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<OAuthStatePayload>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }
}
