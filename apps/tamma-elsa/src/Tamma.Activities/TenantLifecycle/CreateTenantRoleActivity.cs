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
/// Step 3 of <c>CreateTenantWorkflow</c>. Creates the per-tenant Postgres
/// login role <c>tamma_tenant_&lt;hex&gt;</c> with a freshly-generated
/// password — on the ASSIGNED pool row's cluster (roles are
/// cluster-scoped, so the DDL must run where the tenant was placed, not
/// on the central admin connection). Unified-tenancy Phase 2 delegates
/// the step logic to <see cref="ITenantProvisioningService.CreateRoleAsync"/>
/// so the SaaS workflow and the single-user middleware mint roles through
/// the SAME implementation. Idempotent via a <c>pg_roles</c> probe on the
/// target cluster; the service returns <c>null</c> on the skip and this
/// activity converts that to an empty-string
/// <see cref="GeneratedPassword"/> (the workflow-variable contract — no
/// recovery of an existing password, by design; if the role exists from a
/// prior partial run, the operator must drop it first).
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
    "CREATE ROLE tamma_tenant_<hex> with a fresh password on the assigned pool cluster (idempotent).",
    Kind = ActivityKind.Task)]
public sealed class CreateTenantRoleActivity : TenantLifecycleActivity
{
    public override string StepName => "create-role";

    [Input(Description = "Assigned pool row id (output of AssignTenantPlacementActivity).")]
    public Input<string> DatabaseId { get; set; } = default!;

    [Input(Description = "Assigned schema name (output of AssignTenantPlacementActivity).")]
    public Input<string> SchemaName { get; set; } = default!;

    [Output(
        Description = "The plaintext password generated for the new role. "
                      + "Sensitive — workflow must not persist this in the journal. "
                      + "Empty string on idempotent-skip (role already existed).")]
    public Output<string> GeneratedPassword { get; set; } = default!;

    [Output(Description = "Canonical role name (tamma_tenant_<hex>).")]
    public Output<string> RoleName { get; set; } = default!;

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var placement = AssignTenantPlacementActivity.ReconstructPlacement(
            DatabaseId.Get(context), SchemaName.Get(context), "CreateTenantRole");

        var password = await context.GetRequiredService<ITenantProvisioningService>()
            .CreateRoleAsync(tenantId, placement, context.CancellationToken);

        RoleName.Set(context, TenantNaming.RoleName(tenantId));
        // null (idempotent-skip — the service logged it) → empty string:
        // the workflow-variable contract downstream steps key off. The
        // encrypt step fail-fasts when it cannot read a stored password
        // envelope from a prior attempt (operator runbook: DROP OWNED BY
        // on the placement database, DROP ROLE, retry provisioning).
        GeneratedPassword.Set(context, password ?? string.Empty);
    }

    /// <summary>
    /// 32-byte cryptographically-strong password using a Postgres-safe
    /// alphabet. Unified-tenancy Phase 2 extracted the implementation to
    /// <see cref="TenantRolePassword.Generate"/> (Tamma.Data, next to
    /// <see cref="TenantNaming"/>) so the shared
    /// <c>TenantProvisioningService</c> step engine mints from the SAME
    /// generator; this thin alias keeps the activity's historical test
    /// surface intact.
    /// </summary>
    internal static string GenerateStrongPassword() => TenantRolePassword.Generate();
}
