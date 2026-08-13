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
/// <para>Unified-tenancy Phase 2 — the workflow provisions a
/// schema-per-tenant placement instead of a database-per-tenant: an
/// <see cref="AssignTenantPlacementActivity"/> picks the
/// <c>tenant_databases</c> pool row by plan tier, the role + schema DDL
/// runs on THAT row's cluster through the shared
/// <c>ITenantProvisioningService</c> step engine (the same one the
/// single-user middleware uses), and the minted connection string
/// carries <c>Search Path=t_&lt;hex&gt;</c>.</para>
///
/// <para>The activities executed in order:</para>
///
/// <list type="number">
///   <item><description><see cref="MarkProvisioningActivity"/></description></item>
///   <item><description><see cref="AssignTenantPlacementActivity"/> —
///     outputs the DatabaseId + SchemaName variables the next three
///     steps reconstruct the placement from.</description></item>
///   <item><description><see cref="CreateTenantRoleActivity"/></description></item>
///   <item><description><see cref="CreateTenantSchemaActivity"/></description></item>
///   <item><description><see cref="BuildTenantConnectionStringActivity"/> —
///     mints the per-tenant connection string. Stored in a workflow
///     variable that lives only in the in-memory journal slot scrubbed
///     by the platform-event sanitiser.</description></item>
///   <item><description><see cref="MigrateTenantDatabaseActivity"/></description></item>
///   <item><description><see cref="SeedTenantDefaultsActivity"/></description></item>
///   <item><description><see cref="EncryptAndPersistConnectionStringActivity"/></description></item>
///   <item><description><see cref="WarmTenantPoolActivity"/></description></item>
///   <item><description><see cref="MarkTenantActiveActivity"/> — emits the
///     terminal <c>TENANT.CREATED.SUCCESS</c> event.</description></item>
///   <item><description><see cref="QueueWelcomeEmailActivity"/></description></item>
/// </list>
/// </summary>
public class CreateTenantWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Create Tenant";
        builder.DefinitionId = "create-tenant";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description =
            "Provision a new tenant: placement + role + schema + migration + encrypted creds + activate.";

        // ── Workflow variables ───────────────────────────────────────────
        var tenantId = builder.WithVariable<Guid>("TenantId", Guid.Empty).Persisted();
        var attempt = builder.WithVariable<int>("Attempt", 1).Persisted();
        var databaseId = builder.WithVariable<string>("DatabaseId", "").Persisted();
        var schemaName = builder.WithVariable<string>("SchemaName", "").Persisted();
        var roleName = builder.WithVariable<string>("RoleName", "").Persisted();
        var generatedPassword = builder.WithVariable<string>("GeneratedPassword", "").Persisted();
        var tenantConnectionString = builder.WithVariable<string>("TenantConnectionString", "").Persisted();

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

        // ── Step 2: assign placement (outputs pool row id + schema) ──────
        var assignPlacement = new AssignTenantPlacementActivity
        {
            Id = "AssignTenantPlacement",
            Name = "Assign Tenant Placement",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseId = new Output<string>(databaseId),
            SchemaName = new Output<string>(schemaName),
        };

        // ── Step 3: create role on the placement cluster ─────────────────
        var createRole = new CreateTenantRoleActivity
        {
            Id = "CreateTenantRole",
            Name = "Create Tenant Role",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseId = new Input<string>(ctx => databaseId.Get(ctx)),
            SchemaName = new Input<string>(ctx => schemaName.Get(ctx)),
            RoleName = new Output<string>(roleName),
            GeneratedPassword = new Output<string>(generatedPassword),
        };

        // ── Step 4: create schema + grants on the placement database ────
        var createSchema = new CreateTenantSchemaActivity
        {
            Id = "CreateTenantSchema",
            Name = "Create Tenant Schema",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseId = new Input<string>(ctx => databaseId.Get(ctx)),
            SchemaName = new Input<string>(ctx => schemaName.Get(ctx)),
        };

        // ── Step 5: mint the per-tenant connection string ────────────────
        var buildConnectionString = new BuildTenantConnectionStringActivity
        {
            Id = "BuildTenantConnectionString",
            Name = "Build Tenant Connection String",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            DatabaseId = new Input<string>(ctx => databaseId.Get(ctx)),
            SchemaName = new Input<string>(ctx => schemaName.Get(ctx)),
            Password = new Input<string>(ctx => generatedPassword.Get(ctx)),
            ConnectionString = new Output<string>(tenantConnectionString),
        };

        // ── Step 6: migrate ──────────────────────────────────────────────
        var migrateDatabase = new MigrateTenantDatabaseActivity
        {
            Id = "MigrateTenantDatabase",
            Name = "Migrate Tenant Database",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 7: seed defaults ───────────────────────────────────────
        var seedDefaults = new SeedTenantDefaultsActivity
        {
            Id = "SeedTenantDefaults",
            Name = "Seed Tenant Defaults",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 8: encrypt + persist creds ─────────────────────────────
        var encryptAndPersist = new EncryptAndPersistConnectionStringActivity
        {
            Id = "EncryptAndPersist",
            Name = "Encrypt and Persist",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            TenantConnectionString = new Input<string>(ctx => tenantConnectionString.Get(ctx)),
        };

        // ── Step 9: warm pool ───────────────────────────────────────────
        var warmPool = new WarmTenantPoolActivity
        {
            Id = "WarmTenantPool",
            Name = "Warm Tenant Pool",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step 10: mark active + emit TENANT.CREATED.SUCCESS ──────────
        var markActive = new MarkTenantActiveActivity
        {
            Id = "MarkTenantActive",
            Name = "Mark Tenant Active",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        // ── Step 11: queue welcome email (CP outbox, exactly-once) ──────
        // Story 28-5 AC2 step-10 + AC5 — runs AFTER the tenant is active so
        // a failed/aborted provision never sends a welcome. Idempotent +
        // non-fatal (see QueueWelcomeEmailActivity).
        var queueWelcome = new QueueWelcomeEmailActivity
        {
            Id = "QueueWelcomeEmail",
            Name = "Queue Welcome Email",
            TenantId = new Input<Guid>(ctx => tenantId.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
        };

        builder.Root = new Sequence
        {
            Activities =
            {
                initInputs,
                markProvisioning,
                assignPlacement,
                createRole,
                createSchema,
                buildConnectionString,
                migrateDatabase,
                seedDefaults,
                encryptAndPersist,
                warmPool,
                markActive,
                queueWelcome,
            },
        };
    }
}
