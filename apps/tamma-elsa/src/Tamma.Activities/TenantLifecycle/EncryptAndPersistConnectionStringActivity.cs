using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data;
using Tamma.Data.Abstractions;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 6 of <c>CreateTenantWorkflow</c>. Encrypts the per-tenant
/// connection string with the active KEK and writes the envelope to
/// <c>tenants.EncryptedConnectionString</c> + <c>tenants.KekVersion</c>.
/// Idempotent: re-encrypting with the same plaintext is safe; if the
/// envelope is already populated and matches, the activity is a no-op.
///
/// <para>Compensation: <see cref="DeleteTenantWorkflow"/> nulls the
/// column on cleanup; per Doc 04 §6.3 step J. The compensator here
/// (used when later steps fail) sets the columns back to NULL.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Encrypt and Persist Connection String",
    "AES-encrypt the tenant connection string and write to tenants.EncryptedConnectionString.",
    Kind = ActivityKind.Task)]
public sealed class EncryptAndPersistConnectionStringActivity : TenantLifecycleActivity
{
    public override string StepName => "encrypt-creds";

    [Input(Description = "Per-tenant connection string to seal.")]
    public Input<string> TenantConnectionString { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var cs = TenantConnectionString.Get(context);
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException(
                "EncryptAndPersist: TenantConnectionString input is empty.");

        var protector = context.GetRequiredService<ITenantConnectionStringProtector>();
        var kek = protector.CurrentKekVersion;

        var factory = context.GetRequiredService<IDbContextFactory<ControlPlaneDbContext>>();
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);

        var tenant = await db.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, context.CancellationToken)
            ?? throw new InvalidOperationException(
                $"EncryptAndPersist: tenant {tenantId} not found in CP.");

        // PR #329 review: enforce the documented no-op skip when the envelope
        // is already populated under the active KEK version. Re-running the
        // activity (workflow replay, or a downstream-step retry that loops
        // back through this step) shouldn't re-encrypt — fresh AES-GCM
        // ciphertext under the same key would invalidate downstream consumers
        // that snapshot the envelope (e.g. cached connection resolvers).
        // We DO re-encrypt when the KEK version differs (rotation in flight)
        // because the rotation re-encrypt loop owns that path explicitly;
        // this just guards the retry-loop case.
        var existingEnvelope = (string?)db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue;
        var existingKek = (int?)(short?)db.Entry(tenant).Property("KekVersion").CurrentValue;
        if (!string.IsNullOrEmpty(existingEnvelope) && existingKek == kek)
        {
            Logger?.LogInformation(
                "tenant.lifecycle.encrypt_creds skipped (idempotent) tenantId={TenantId} kek={Kek}",
                tenantId, kek);
            return;
        }

        var envelope = protector.Encrypt(cs);
        db.Entry(tenant).Property("EncryptedConnectionString").CurrentValue = envelope;
        db.Entry(tenant).Property("KekVersion").CurrentValue = (short)kek;
        tenant.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.encrypt_creds persisted tenantId={TenantId} kek={Kek} envelopeLen={Len}",
            tenantId, kek, envelope.Length);
    }
}
