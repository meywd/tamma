using System.Security.Cryptography;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 2 of <c>CreateTenantWorkflow</c>. Creates the per-tenant Postgres
/// login role <c>tamma_tenant_&lt;hex&gt;</c> with a freshly-generated
/// password. Idempotent via a <c>pg_roles</c> probe; a workflow retry
/// after a partial success returns the existing role's transient
/// password from <see cref="GeneratedPassword"/> only when this activity
/// instance generated it (no recovery of an existing password — by
/// design; if the role exists from a prior partial run, the operator
/// must drop it first).
///
/// <para>Output: <see cref="GeneratedPassword"/> is the plaintext
/// password the workflow needs to hand to
/// <see cref="EncryptAndPersistConnectionStringActivity"/>. It MUST NOT
/// be persisted in the workflow journal — the caller wraps the workflow
/// run with a sanitiser that scrubs this output before serialisation.
/// Until that sanitiser lands, treat the workflow definition's
/// <c>variables</c> for this output as in-memory-only.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Create Tenant Role",
    "CREATE ROLE tamma_tenant_<hex> with a fresh password (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class CreateTenantRoleActivity : TenantLifecycleActivity
{
    public override string StepName => "create-role";

    [Output(
        Description = "The plaintext password generated for the new role. "
                      + "Sensitive — workflow must not persist this in the journal.")]
    public Output<string> GeneratedPassword { get; set; } = default!;

    [Output(Description = "Canonical role name (tamma_tenant_<hex>).")]
    public Output<string> RoleName { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var roleName = TenantNaming.RoleName(tenantId);
        var quoted = TenantNaming.Quote(roleName);

        if (await admin.RoleExistsAsync(roleName, context.CancellationToken))
        {
            // Leave the existing role in place. The encrypted connection
            // string from a prior partial run is the only path to recover
            // the password; if Step 8 hadn't completed, the operator
            // runbook calls for DROP ROLE + retry.
            Logger?.LogInformation(
                "tenant.lifecycle.create_role idempotent_skip tenantId={TenantId} role={Role}",
                tenantId, roleName);
            RoleName.Set(context, roleName);
            // Signal that we did not generate a fresh password this run —
            // the encrypt step will fail-fast if it cannot read a stored
            // password from a prior attempt.
            GeneratedPassword.Set(context, string.Empty);
            return;
        }

        var password = GenerateStrongPassword();

        // Quote the password literal — Postgres allows '...' with '' as
        // an escape for a single quote. Reject any candidate password
        // that contains a single quote so we never need to worry about
        // injection here. The generator below uses [A-Za-z0-9!@#%^*_-]
        // only, so this is defence-in-depth.
        if (password.Contains('\''))
            throw new InvalidOperationException(
                "Generated password contained a quote — refusing to issue CREATE ROLE.");

        var sql =
            $"CREATE ROLE {quoted} WITH LOGIN PASSWORD '{password}' "
            + "NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;";

        await admin.ExecuteAsync(sql, context.CancellationToken);

        RoleName.Set(context, roleName);
        GeneratedPassword.Set(context, password);
        Logger?.LogInformation(
            "tenant.lifecycle.create_role created tenantId={TenantId} role={Role}",
            tenantId, roleName);
    }

    /// <summary>
    /// 32-byte cryptographically-strong password using a Postgres-safe
    /// alphabet. Excludes single-quote, backslash, semicolon — the three
    /// characters that have ever caused trouble inside a quoted SQL
    /// literal — so the CREATE ROLE statement above is safe to build by
    /// concatenation.
    /// </summary>
    internal static string GenerateStrongPassword()
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
                // Reject 00..F when alphabet doesn't divide 256 evenly to
                // avoid modulo bias — alphabet length is 76 here, so we
                // accept bytes < 76 * 3 = 228, otherwise resample.
                if (b >= alphabet.Length * (256 / alphabet.Length)) continue;
                sb.Append(alphabet[b % alphabet.Length]);
            }
        }
        return sb.ToString();
    }
}
