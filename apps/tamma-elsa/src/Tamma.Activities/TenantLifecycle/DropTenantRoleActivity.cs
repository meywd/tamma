using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tamma.Data.Abstractions;
using Tamma.Data.Pooling;

namespace Tamma.Activities.TenantLifecycle;

/// <summary>
/// Step 4 of <c>DeleteTenantWorkflow</c>. Drops the per-tenant Postgres
/// role. <c>DROP OWNED BY</c> first to release any objects the role
/// owned, then <c>DROP ROLE IF EXISTS</c> so a redundant call is silent.
///
/// <para>Sequence requirement: must run AFTER
/// <see cref="DropTenantDatabaseActivity"/>, otherwise <c>DROP OWNED BY</c>
/// fails because the role still owns the database.</para>
///
/// <para><b>Throws</b> on admin-connection failure so the surrounding
/// <c>DeleteTenantWorkflow</c> aborts cleanly. For continue-on-error
/// cleanup semantics see
/// <see cref="DropTenantRoleForCleanupActivity"/>.</para>
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Role",
    "DROP OWNED BY then DROP ROLE IF EXISTS — idempotent.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantRoleActivity : TenantLifecycleActivity
{
    public override string StepName => "drop-role";

    protected override async Task ProcessAsync(
        ActivityExecutionContext context,
        Guid tenantId,
        int attempt)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var roleName = TenantNaming.RoleName(tenantId);
        var quoted = TenantNaming.Quote(roleName);

        if (!await admin.RoleExistsAsync(roleName, context.CancellationToken))
        {
            Logger?.LogInformation(
                "tenant.lifecycle.drop_role idempotent_skip tenantId={TenantId} role={Role}",
                tenantId, roleName);
            return;
        }

        // DROP OWNED BY removes any remaining grants / objects the role
        // holds across other databases (the tenant DB is already gone).
        // It runs in its own statement so a transient grant somewhere
        // doesn't make the DROP ROLE fail.
        await admin.ExecuteAsync(
            $"DROP OWNED BY {quoted};",
            context.CancellationToken);

        await admin.ExecuteAsync(
            $"DROP ROLE IF EXISTS {quoted};",
            context.CancellationToken);

        Logger?.LogInformation(
            "tenant.lifecycle.drop_role completed tenantId={TenantId} role={Role}",
            tenantId, roleName);
    }
}

/// <summary>
/// H6 / Story 28-5 AC7 — continue-on-error variant of
/// <see cref="DropTenantRoleActivity"/> used by
/// <c>CleanUpFailedTenantWorkflow</c>. Same DROP OWNED BY → DROP ROLE
/// sequence; on failure the exception is swallowed and recorded into
/// the workflow's per-step state.
/// </summary>
[Activity(
    "Tamma.TenantLifecycle",
    "Drop Tenant Role (Cleanup)",
    "Continue-on-error variant — never throws; records failure to workflow state.",
    Kind = ActivityKind.Task)]
public sealed class DropTenantRoleForCleanupActivity : CleanupStepActivity
{
    public override string StepName => CleanupSteps.DropRole;

    protected override async Task DoStepAsync(
        ActivityExecutionContext context,
        Guid tenantId)
    {
        var admin = context.GetRequiredService<ITenantAdminConnection>();
        var roleName = TenantNaming.RoleName(tenantId);
        var quoted = TenantNaming.Quote(roleName);

        if (!await admin.RoleExistsAsync(roleName, context.CancellationToken)
            .ConfigureAwait(false))
        {
            Logger?.LogInformation(
                "tenant.cleanup.drop_role idempotent_skip tenantId={TenantId} role={Role}",
                tenantId, roleName);
            return;
        }

        await admin.ExecuteAsync(
            $"DROP OWNED BY {quoted};",
            context.CancellationToken).ConfigureAwait(false);

        await admin.ExecuteAsync(
            $"DROP ROLE IF EXISTS {quoted};",
            context.CancellationToken).ConfigureAwait(false);

        Logger?.LogInformation(
            "tenant.cleanup.drop_role completed tenantId={TenantId} role={Role}",
            tenantId, roleName);
    }
}
