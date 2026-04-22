using System.Text;
using Tamma.Data.Abstractions;

namespace Tamma.Data.Pooling;

/// <summary>
/// Default <see cref="IConnectionStringDecryptor"/> wired by
/// <c>AddTammaData</c>. Treats the envelope bytes as UTF-8 cleartext —
/// useful for local-laptop dev where the column is populated by hand or
/// by an unencrypted seeder.
///
/// <para>Production deployments MUST replace this binding with the real
/// AES-GCM-backed implementation that wraps
/// <c>TenantSecretProtector</c>; see Story 28-12 for the rotation
/// machinery and the API composition root for the registration override.</para>
/// </summary>
public sealed class PassthroughConnectionStringDecryptor : IConnectionStringDecryptor
{
    public string Decrypt(byte[] envelope, int? kekVersion)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Length == 0)
        {
            throw new ArgumentException(
                "Envelope is empty — passthrough decryptor expects non-empty UTF-8 bytes.",
                nameof(envelope));
        }

        return Encoding.UTF8.GetString(envelope);
    }
}
