using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// The single (de)serialization seam for the email credential BUNDLE stored as a
/// cabinet secret's plaintext. One source of truth so the write endpoint
/// (serialize) and the resolver (deserialize) cannot drift on the JSON shape.
/// </summary>
public static class EmailCredentialCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialize a bundle to the cabinet plaintext JSON.</summary>
    public static string Serialize(EmailCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return JsonSerializer.Serialize(
            new Bundle(
                credential.Transport,
                credential.From,
                credential.ResendApiKey,
                credential.SmtpHost,
                credential.SmtpPort,
                credential.SmtpUsername,
                credential.SmtpPassword,
                credential.SmtpUseStartTls),
            Options);
    }

    /// <summary>
    /// Parse a stored bundle back into an <see cref="EmailCredential"/>. Returns
    /// null when the JSON is malformed, the transport is unknown, or the
    /// transport-required secret / <c>from</c> is missing — the resolver treats
    /// that as "credential absent" (never a partial credential).
    /// </summary>
    public static EmailCredential? TryDeserialize(string? json)
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
            || string.IsNullOrWhiteSpace(bundle.Transport)
            || string.IsNullOrWhiteSpace(bundle.From))
        {
            return null;
        }

        var transport = bundle.Transport.Trim().ToLowerInvariant();
        var credential = new EmailCredential(
            transport,
            bundle.From.Trim(),
            bundle.ResendApiKey,
            bundle.SmtpHost,
            bundle.SmtpPort,
            bundle.SmtpUsername,
            bundle.SmtpPassword,
            bundle.SmtpUseStartTls);

        return IsComplete(credential) ? credential : null;
    }

    /// <summary>
    /// A bundle is usable when its transport-required secret is present:
    /// resend ⇒ <c>resendApiKey</c>; smtp ⇒ <c>smtpHost</c>. An unknown transport
    /// is rejected.
    /// </summary>
    public static bool IsComplete(EmailCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.Transport switch
        {
            EmailCredential.TransportResend => !string.IsNullOrWhiteSpace(credential.ResendApiKey),
            EmailCredential.TransportSmtp => !string.IsNullOrWhiteSpace(credential.SmtpHost),
            _ => false,
        };
    }

    private sealed record Bundle(
        string? Transport,
        string? From,
        string? ResendApiKey,
        string? SmtpHost,
        int? SmtpPort,
        string? SmtpUsername,
        string? SmtpPassword,
        bool? SmtpUseStartTls);
}
