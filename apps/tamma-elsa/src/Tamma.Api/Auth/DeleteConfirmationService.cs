using System.Security.Cryptography;
using System.Text;

namespace Tamma.Api.Auth;

/// <summary>
/// HMAC-SHA256 generate/verify for the two-phase tenant delete flow
/// (finding 021). Mirrors the TS
/// <c>generateDeleteConfirmation</c> / <c>verifyDeleteConfirmation</c>
/// helpers in <c>packages/api/src/routes/orgs/index.ts</c>.
///
/// <para>Token shape: <c>{issuedAtMs}.{hmacHex}</c>. The HMAC covers
/// <c>{tenantId}:{userId}:{issuedAtMs}</c> with the JWT signing secret as
/// the key. TTL is 10 minutes. The token is delivered to the caller as
/// the <c>confirmationToken</c> field of the phase-1 response; the caller
/// then re-requests the DELETE with <c>?confirm=&lt;token&gt;</c>.</para>
/// </summary>
public interface IDeleteConfirmationService
{
    /// <summary>Mints a fresh confirmation token + expiry.</summary>
    DeleteConfirmation Generate(Guid tenantId, Guid userId);

    /// <summary>
    /// Constant-time verify. Returns <c>true</c> iff the token is well-formed,
    /// unexpired, and binds to the supplied <paramref name="tenantId"/> and
    /// <paramref name="userId"/>.
    /// </summary>
    bool Verify(string? token, Guid tenantId, Guid userId);
}

public readonly record struct DeleteConfirmation(string Token, DateTime ExpiresAt);

public sealed class DeleteConfirmationService : IDeleteConfirmationService
{
    private const int TtlMinutes = 10;
    private readonly byte[] _secret;

    public DeleteConfirmationService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret not configured");
        _secret = Encoding.UTF8.GetBytes(secret);
    }

    public DeleteConfirmation Generate(Guid tenantId, Guid userId)
    {
        var issuedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = $"{tenantId:D}:{userId:D}:{issuedAt}";
        var hmac = Convert.ToHexString(
            HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();

        var token = $"{issuedAt}.{hmac}";
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(issuedAt)
            .UtcDateTime.AddMinutes(TtlMinutes);
        return new DeleteConfirmation(token, expiresAt);
    }

    public bool Verify(string? token, Guid tenantId, Guid userId)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var dot = token.IndexOf('.');
        if (dot <= 0 || dot >= token.Length - 1) return false;

        var issuedAtRaw = token[..dot];
        var providedHmac = token[(dot + 1)..];

        if (!long.TryParse(issuedAtRaw, out var issuedAt)) return false;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ttlMs = (long)TimeSpan.FromMinutes(TtlMinutes).TotalMilliseconds;
        if (nowMs - issuedAt > ttlMs) return false;
        if (issuedAt > nowMs + 60_000) return false; // future-dated > 1 min

        var payload = $"{tenantId:D}:{userId:D}:{issuedAt}";
        var expectedBytes = HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(payload));
        var expectedHmac = Convert.ToHexString(expectedBytes).ToLowerInvariant();

        // Constant-time compare
        if (providedHmac.Length != expectedHmac.Length) return false;
        var providedBytes = Encoding.ASCII.GetBytes(providedHmac);
        var expectedAsciiBytes = Encoding.ASCII.GetBytes(expectedHmac);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedAsciiBytes);
    }
}
