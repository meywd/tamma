using System.Security.Cryptography;

namespace Tamma.Data.Pooling;

/// <summary>
/// Unified-tenancy Phase 2 — canonical generator for per-tenant Postgres
/// role passwords. Extracted from
/// <c>CreateTenantRoleActivity.GenerateStrongPassword</c> so the shared
/// <c>TenantProvisioningService</c> step engine and the Elsa activity
/// mint passwords from the SAME implementation (one behavior, two
/// entry points). Lives next to <see cref="TenantNaming"/> because the
/// two together define everything about the tenant role's identity.
/// </summary>
public static class TenantRolePassword
{
    /// <summary>
    /// 32-byte cryptographically-strong password using a Postgres-safe
    /// alphabet. Excludes single-quote, backslash, semicolon — the three
    /// characters that have ever caused trouble inside a quoted SQL
    /// literal — so a <c>CREATE ROLE ... PASSWORD '...'</c> statement is
    /// safe to build by concatenation.
    /// </summary>
    public static string Generate()
    {
        const string alphabet =
            "ABCDEFGHJKLMNPQRSTUVWXYZ" + "abcdefghijkmnopqrstuvwxyz" + "23456789" + "!@#%^*_-";
        const int length = 32;

        Span<byte> bytes = stackalloc byte[length * 2];
        var sb = new System.Text.StringBuilder(length);
        while (sb.Length < length)
        {
            RandomNumberGenerator.Fill(bytes);
            for (var i = 0; i < bytes.Length && sb.Length < length; i++)
            {
                var b = bytes[i];
                // Reject the top sliver when the alphabet doesn't divide
                // 256 evenly to avoid modulo bias — alphabet length is 65
                // here, so we accept bytes < 65 * 3 = 195, else resample.
                if (b >= alphabet.Length * (256 / alphabet.Length)) continue;
                sb.Append(alphabet[b % alphabet.Length]);
            }
        }
        return sb.ToString();
    }
}
