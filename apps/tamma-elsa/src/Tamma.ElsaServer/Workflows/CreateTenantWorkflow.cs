using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 28-5 — global Elsa workflow that provisions a new tenant. Triggered
/// by a correlated <c>TENANT.PROVISIONING_REQUESTED</c> signal that the
/// verify-email endpoint emits after flipping the tenant from
/// <c>pending_verification</c> to <c>provisioning</c>.
///
/// <para>The flow is intentionally linear (a <see cref="Sequence"/>): each
/// step is idempotent so an Elsa restart mid-workflow re-runs the activity
/// without harm. Failure throws — the workflow restart logic kicks the
/// activity back into the queue at <c>Attempt+1</c>; per-step retry caps
/// + compensation ladder are owned by the call-site rather than this
/// definition (see <see cref="DeleteTenantWorkflow"/> for the symmetric
/// teardown invoked on terminal failure).</para>
///
/// <para>The eight activities executed in order:</para>
///
/// <list type="number">
///   <item><description><see cref="MarkProvisioningActivity"/></description></item>
///   <item><description><see cref="CreateTenantRoleActivity"/></description></item>
///   <item><description><see cref="CreateTenantDatabaseActivity"/></description></item>
///   <item><description>Inline build of the per-tenant connection string
///     from the role, password, and admin host/port via the same
///     <see cref="Tamma.Data.Abstractions.ITenantAdminConnection"/>
///     resolved by <see cref="CreateTenantRoleActivity"/>. Stored in a
///     workflow variable that lives only in the in-memory journal slot
///     scrubbed by the platform-event sanitiser.</description></item>
///   <item><description><see cref="MigrateTenantDatabaseActivity"/></description></item>
///   <item><description><see cref="SeedTenantDefaultsActivity"/></description></item>
///   <item><description><see cref="EncryptAndPersistConnectionStringActivity"/></description></item>
///   <item><description><see cref="WarmTenantPoolActivity"/></description></item>
///   <item><description><see cref="MarkTenantActiveActivity"/> — emits the
///     terminal <c>TENANT.CREATED.SUCCESS</c> event.</description></item>
/// </list>
///
/// <para>The connection-string assembly happens in <see cref="BuildTenantConnectionStringActivity"/>
/// (a thin code activity defined alongside) so the workflow shape stays
/// declarative and unit-testable in isolation.</para>
/// </summary>
public class CreateTenantWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Create Tenant";
        builder.DefinitionId = "create-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Provision a new tenant: role + database + migration + encrypted creds + activate.";

        // ── Workflow variables ───────────────────────────────────────────
        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty);
        var attempt = builder.WithVariable<int>("Attempt", 1);
        var roleName = builder.WithVariable<string>("RoleName", "");
        var generatedPassword = builder.WithVariable<string>("GeneratedPassword", "");
        var databaseName = builder.WithVariable<string>("DatabaseName", "");
        var tenantConnectionString = builder.WithVariable<string>("TenantConnectionString", "");

        // ── Inputs ────────────────────────────────────────────────────────
        var initInputs = new SetVariable
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Variable = tenantId,
            Value = new Input<object?>(ctx =>
            {
                var raw = ctx.GetInput<object?>("tenantId");
                var parsed = raw switch
                {
                    Guid g => g,
                    string s when Guid.TryParse(s, out var p) => p,
                    _ => Guid.Empty,
                };
                if (parsed == Guid.Empty)
                    throw new InvalidOperationException(
                        "CreateTenantWorkflow input 'tenantId' is required and must be a non-empty Guid.");

                var attemptIn = ctx.GetInput<int?>("attempt") ?? 1;
                attempt.Set(ctx, attemptIn <= 0 ? 1 : attemptIn);

                return parsed;
            }),
        };

        // ── Step 1: mark provisioning ────────────────────────────────────
        var markProvisioning = new MarkProvisioningActivity
        {
            Id = "MarkProvisioning",
            Name = "Mark Provisioning",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step 2: create role (outputs role + password) ────────────────
        var createRole = new CreateTenantRoleActivity
        {
            Id = "CreateTenantRole",
            Name = "Create Tenant Role",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            RoleName = new Output<string>(roleName),
            GeneratedPassword = new Output<string>(generatedPassword),
        };

        // ── Step 3: create database (outputs database name) ──────────────
        var createDatabase = new CreateTenantDatabaseActivity
        {
            Id = "CreateTenantDatabase",
            Name = "Create Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseName = new Output<string>(databaseName),
        };

        // ── Step 4: assemble the per-tenant connection string ───────────
        var buildConnectionString = new BuildTenantConnectionStringActivity
        {
            Id = "BuildTenantConnectionString",
            Name = "Build Tenant Connection String",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseName = new Input<string>(ctx => databaseName.Get(ctx)),
            RoleName = new Input<string>(ctx => roleName.Get(ctx)),
            Password = new Input<string>(ctx => generatedPassword.Get(ctx)),
            ConnectionString = new Output<string>(tenantConnectionString),
        };

        // ── Step 5: migrate ──────────────────────────────────────────────
        var migrateDatabase = new MigrateTenantDatabaseActivity
        {
            Id = "MigrateTenantDatabase",
            Name = "Migrate Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 6: seed defaults ───────────────────────────────────────
        var seedDefaults = new SeedTenantDefaultsActivity
        {
            Id = "SeedTenantDefaults",
            Name = "Seed Tenant Defaults",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 7: encrypt + persist creds ─────────────────────────────
        var encryptAndPersist = new EncryptAndPersistConnectionStringActivity
        {
            Id = "EncryptAndPersist",
            Name = "Encrypt and Persist",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 8: warm pool ───────────────────────────────────────────
        var warmPool = new WarmTenantPoolActivity
        {
            Id = "WarmTenantPool",
            Name = "Warm Tenant Pool",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step 9: mark active + emit TENANT.CREATED.SUCCESS ──────────
        var markActive = new MarkTenantActiveActivity
        {
            Id = "MarkTenantActive",
            Name = "Mark Tenant Active",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initInputs,
                markProvisioning,
                createRole,
                createDatabase,
                buildConnectionString,
                migrateDatabase,
                seedDefaults,
                encryptAndPersist,
                warmPool,
                markActive,
            },
        };
    }
}
