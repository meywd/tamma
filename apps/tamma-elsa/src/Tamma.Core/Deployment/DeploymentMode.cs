namespace Tamma.Core.Deployment;

/// <summary>
/// The deployment <c>mode</c> wire token threaded from the autonomous loop
/// (<c>AdlOrchestratorWorkflow → DispatchCycleActivity → single-issue-cycle →
/// deployment-pipeline</c>) into the deployment pipeline's production-approval
/// gate.
///
/// <para><b>Why this lives in Tamma.Core (not Tamma.Api).</b> The real
/// process-wide operating-mode source of truth is
/// <c>Tamma.Api.Services.PromptStore.ITammaModeProvider</c>, but the Elsa
/// engine layer (<c>Tamma.Activities</c> / <c>Tamma.ElsaServer</c>) cannot
/// reference <c>Tamma.Api</c> without re-introducing the dependency cycle that
/// Story 27-19 broke. So this resolver re-derives the SAME single-vs-SaaS
/// decision from the SAME configuration signals that <c>TammaModeProvider</c>
/// reads (<c>Tamma:Mode</c> explicit override, else the presence of the SaaS-only
/// <c>Tamma:TenantSharedSecret</c> / <c>ConnectionStrings:ControlPlane</c>). The
/// engine reads those values from its injected <c>IConfiguration</c> and calls
/// the pure helper here — keeping the detection logic identical and unit-testable
/// without taking a dependency on the Configuration package in Tamma.Core.</para>
/// </summary>
public static class DeploymentMode
{
    /// <summary>
    /// SaaS / Business-Mode token. The deployment pipeline's
    /// <c>ProdApprovalNeeded</c> gate engages the human approval bookmark on
    /// <c>mode == "business"</c> — so SaaS deployments are gated by default.
    /// </summary>
    public const string Business = "business";

    /// <summary>
    /// Single-user / self-hosted token. Dev mode deploys to production without
    /// a human gate (unless an operator forces it via
    /// <c>Deployment:RequireProdApproval</c>).
    /// </summary>
    public const string Dev = "dev";

    /// <summary>
    /// Resolve the deployment <c>mode</c> wire token from the three configuration
    /// signals (read by the caller from <c>IConfiguration</c>):
    /// <list type="number">
    ///   <item>an explicit <c>Tamma:Mode</c> override (<c>saas</c> → business,
    ///     <c>single-user</c> → dev) wins;</item>
    ///   <item>otherwise, the presence of EITHER SaaS-only signal
    ///     (<c>Tamma:TenantSharedSecret</c> or <c>ConnectionStrings:ControlPlane</c>)
    ///     → business;</item>
    ///   <item>otherwise → dev (self-hosted single-user default).</item>
    /// </list>
    ///
    /// <para><b>Fail-safe:</b> an explicit-mode value that is present but
    /// unrecognised resolves to <see cref="Business"/> (REQUIRE approval) rather
    /// than silently bypassing the gate — a mis-typed mode must never auto-deploy
    /// to prod. Mirrors <c>TammaModeProvider.Resolve</c>'s detection but is
    /// fail-safe instead of throwing (the engine has no good place to surface a
    /// startup throw, and the safe default is to GATE).</para>
    /// </summary>
    /// <param name="explicitMode">Value of <c>Tamma:Mode</c> (nullable).</param>
    /// <param name="hasTenantSharedSecret">True when <c>Tamma:TenantSharedSecret</c> is set.</param>
    /// <param name="hasControlPlaneConnection">True when <c>ConnectionStrings:ControlPlane</c> is set.</param>
    public static string Resolve(
        string? explicitMode,
        bool hasTenantSharedSecret,
        bool hasControlPlaneConnection)
    {
        if (!string.IsNullOrWhiteSpace(explicitMode))
        {
            return explicitMode.Trim().ToLowerInvariant() switch
            {
                "saas" or "business" => Business,
                "single-user" or "singleuser" or "single_user" or "dev" => Dev,
                // Fail-safe: an unrecognised explicit mode REQUIRES approval — never
                // a silent prod auto-deploy.
                _ => Business,
            };
        }

        // Inferred from SaaS-only config presence — same signals TammaModeProvider
        // uses. Either present → SaaS → Business Mode (gated).
        return (hasTenantSharedSecret || hasControlPlaneConnection)
            ? Business
            : Dev;
    }
}
