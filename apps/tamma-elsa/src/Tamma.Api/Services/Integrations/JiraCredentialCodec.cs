using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// The single (de)serialization seam for the JIRA credential BUNDLE stored as a
/// cabinet secret's plaintext. Kept as one source of truth so the write endpoint
/// (serialize) and the resolver (deserialize) cannot drift on the JSON shape.
/// </summary>
public static class JiraCredentialCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize a bundle to the cabinet plaintext JSON.</summary>
    public static string Serialize(JiraCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return JsonSerializer.Serialize(
            new Bundle(credential.BaseUrl, credential.Email, credential.ApiToken),
            Options);
    }

    /// <summary>
    /// Parse a stored bundle back into a <see cref="JiraCredential"/>. Returns
    /// null when the JSON is malformed or any required field is missing/blank —
    /// the resolver treats that as "credential absent" and moves to the next tier
    /// (never a partial credential).
    /// </summary>
    public static JiraCredential? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        Bundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<Bundle>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (bundle is null
            || string.IsNullOrWhiteSpace(bundle.BaseUrl)
            || string.IsNullOrWhiteSpace(bundle.Email)
            || string.IsNullOrWhiteSpace(bundle.ApiToken))
        {
            return null;
        }

        return new JiraCredential(bundle.BaseUrl.Trim(), bundle.Email.Trim(), bundle.ApiToken);
    }

    private sealed record Bundle(string? BaseUrl, string? Email, string? ApiToken);
}
