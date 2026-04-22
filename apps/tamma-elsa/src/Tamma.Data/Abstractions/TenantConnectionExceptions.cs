namespace Tamma.Data.Abstractions;

/// <summary>
/// Thrown by <see cref="ITenantConnectionResolver"/> when a tenant id
/// has no row in the control-plane <c>tenants</c> table. Distinct from
/// <see cref="TenantNotProvisionedException"/> — that one signals "the
/// row exists but isn't usable yet".
/// </summary>
public sealed class TenantNotFoundException : Exception
{
    public Guid TenantId { get; }

    public TenantNotFoundException(Guid tenantId)
        : base($"Tenant '{tenantId}' was not found in the control plane.")
    {
        TenantId = tenantId;
    }
}

/// <summary>
/// Thrown by <see cref="ITenantConnectionResolver"/> when the tenant
/// row exists but its lifecycle state forbids issuing a connection — for
/// example <c>Status='provisioning'</c> (DB not yet ready) or
/// <c>Status='deleted'</c> (cooling-off complete).
/// </summary>
public sealed class TenantNotProvisionedException : Exception
{
    public Guid TenantId { get; }
    public string? Status { get; }

    public TenantNotProvisionedException(Guid tenantId, string? status)
        : base($"Tenant '{tenantId}' is not in a state that accepts connections (status: {status ?? "<null>"}).")
    {
        TenantId = tenantId;
        Status = status;
    }
}

/// <summary>
/// Thrown when <see cref="IConnectionStringDecryptor.Decrypt"/> fails
/// (auth-tag mismatch, key-version unrecognised, envelope corrupt).
/// Never carries the envelope contents — the message is intentionally
/// terse so the encrypted secret cannot leak via logs.
/// </summary>
public sealed class TenantConnectionDecryptionException : Exception
{
    public Guid TenantId { get; }

    public TenantConnectionDecryptionException(Guid tenantId)
        : base($"Failed to decrypt connection-string envelope for tenant '{tenantId}'.")
    {
        TenantId = tenantId;
    }

    public TenantConnectionDecryptionException(Guid tenantId, Exception innerException)
        : base(
            $"Failed to decrypt connection-string envelope for tenant '{tenantId}'.",
            innerException)
    {
        TenantId = tenantId;
    }
}

/// <summary>
/// Thrown when the row's <c>EncryptedConnectionString</c> column is null
/// or empty for a tenant whose <c>Status</c> claims it should be
/// resolvable. Indicates a bug in the provisioning pipeline.
/// </summary>
public sealed class TenantConnectionStringMissingException : Exception
{
    public Guid TenantId { get; }

    public TenantConnectionStringMissingException(Guid tenantId)
        : base(
            $"Tenant '{tenantId}' is marked active but has no encrypted "
            + "connection string. Provisioning bug — re-run provisioning "
            + "or fix the row by hand.")
    {
        TenantId = tenantId;
    }
}
