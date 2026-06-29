namespace Tamma.Api.Services.Provisioning.V2;

/// <summary>
/// Discriminates the lifecycle action a <see cref="ProvisionTenantV2TaskPayload"/>
/// carries on the platform queue. Story 30-9 (reduced to Cranl+Null scope by
/// Epic 30 Phase A): provision and deprovision ride the SAME queue type
/// (<see cref="ProvisionTenantV2TaskPayload.TaskType"/>) + handler, branched
/// on this flag.
/// </summary>
public enum ProvisioningOperation
{
    /// <summary>Default — run the 8-step provisioning saga.</summary>
    Provision,

    /// <summary>Reverse path — tear down provisioned infrastructure.</summary>
    Deprovision
}
