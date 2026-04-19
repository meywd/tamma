namespace Tamma.Api.Services.Provisioning;

/// <summary>
/// State machine for per-tenant provisioning. Persisted on
/// <c>tenants.provisioning_state</c> as a lower-snake_case string so the
/// column is human-readable in psql and round-trippable through the
/// <see cref="ParseState"/> / <see cref="ToStorageString"/> helpers.
///
/// <para>Transitions:</para>
/// <code>
///   None
///     → Pending (admin POST received, background task enqueued)
///       → DatabaseProvisioning (Cranl create-database succeeded, polling)
///         → DatabaseReady (db.status == "running")
///           → AppProvisioning (Cranl create-application succeeded)
///             → AppDeploying (env vars pushed, deploy triggered, polling)
///               → Ready (app.status == "running" + domains fetched)
///   any state → Failed (with detail) → terminal until manual reset
///   Ready → Deprovisioning → Deprovisioned (tear-down completed)
/// </code>
/// </summary>
public enum ProvisioningState
{
    /// <summary>Tenant has not been provisioned. Default for new rows.</summary>
    None,

    /// <summary>Provisioning task accepted; background work has not started yet.</summary>
    Pending,

    /// <summary>Cranl project created; database create call in flight or polling.</summary>
    DatabaseProvisioning,

    /// <summary>Database reports <c>status == "running"</c>; connection string captured.</summary>
    DatabaseReady,

    /// <summary>Cranl application created; environment vars push in flight.</summary>
    AppProvisioning,

    /// <summary>Application <c>POST /deploy</c> issued; polling app status.</summary>
    AppDeploying,

    /// <summary>Application reports <c>status == "running"</c>; default domain captured.</summary>
    Ready,

    /// <summary>Terminal failure. <c>provisioning_detail</c> carries the diagnostic.</summary>
    Failed,

    /// <summary>Teardown in progress (delete app → db → project).</summary>
    Deprovisioning,

    /// <summary>Teardown completed; Cranl identifiers cleared from the row.</summary>
    Deprovisioned
}

public static class ProvisioningStateExtensions
{
    public static string ToStorageString(this ProvisioningState state) => state switch
    {
        ProvisioningState.None => "none",
        ProvisioningState.Pending => "pending",
        ProvisioningState.DatabaseProvisioning => "database_provisioning",
        ProvisioningState.DatabaseReady => "database_ready",
        ProvisioningState.AppProvisioning => "app_provisioning",
        ProvisioningState.AppDeploying => "app_deploying",
        ProvisioningState.Ready => "ready",
        ProvisioningState.Failed => "failed",
        ProvisioningState.Deprovisioning => "deprovisioning",
        ProvisioningState.Deprovisioned => "deprovisioned",
        _ => "none"
    };

    public static ProvisioningState ParseState(string? raw) => raw switch
    {
        "none" or null or "" => ProvisioningState.None,
        "pending" => ProvisioningState.Pending,
        "database_provisioning" => ProvisioningState.DatabaseProvisioning,
        "database_ready" => ProvisioningState.DatabaseReady,
        "app_provisioning" => ProvisioningState.AppProvisioning,
        "app_deploying" => ProvisioningState.AppDeploying,
        "ready" => ProvisioningState.Ready,
        "failed" => ProvisioningState.Failed,
        "deprovisioning" => ProvisioningState.Deprovisioning,
        "deprovisioned" => ProvisioningState.Deprovisioned,
        _ => ProvisioningState.None
    };
}

/// <summary>Caller-supplied options for kicking off provisioning.</summary>
public sealed record ProvisioningOptions(string Region, string? CustomName = null);

/// <summary>Snapshot of the current provisioning state for a tenant.</summary>
public sealed record ProvisioningStatus(
    ProvisioningState State,
    string? Detail,
    string? AppDefaultDomain,
    DateTimeOffset UpdatedAt);
