using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;

namespace Tamma.Activities.ADL;

/// <summary>
/// Epic 31 P2 — THE IS-SUPPORTED CHECK STEP (execution plan §4, owner-decided
/// mechanism): every platform/third-party action in a workflow is an explicit
/// step PRECEDED by this check, and PAIRED with a defined alternative step
/// taken when the check answers unsupported. Support is decided BEFORE the
/// action runs, in workflow structure — not discovered mid-step.
///
/// <para>One reusable activity, parameterized by <see cref="Capability"/>
/// (a <c>PlatformCapability</c> member name, e.g. <c>PrLifecycle</c>). It
/// consults the RESOLVED driver's live capability set through the read-only
/// mediation probe (<c>GET /api/v1/git/{owner}/{repo}/capabilities</c>) — the
/// per-installation, feature-detected answer, never the static matrix alone.
/// P1's capability contract test (advertised ⇔ implemented) is what makes this
/// check trustworthy.</para>
///
/// <para><b>Probe-unreachable semantics</b>: a null/failed probe answers
/// <c>Supported</c> (proceed to the action). Rationale: the action step itself
/// carries the typed <c>capability_unsupported</c> safety-net outcome (defense
/// in depth, §4.3), so proceeding preserves today's behavior exactly when the
/// probe cannot answer — the alternative branch is only taken on a POSITIVE
/// "this platform cannot do it". A probe outage must never silently skip a
/// real action.</para>
///
/// Outcomes:
///   - Supported:   run the action step as today.
///   - Unsupported: route to the action's defined ALTERNATIVE step (which
///                  emits the audit event — silent skips are forbidden).
/// </summary>
[Activity(
    "Tamma.ADL",
    "Check Platform Capability",
    "Is-supported check step before a capability-gated platform action (Epic 31 §4)",
    Kind = ActivityKind.Task
)]
[FlowNode("Supported", "Unsupported")]
public class CheckPlatformCapabilityActivity : Activity
{
    private readonly ILogger<CheckPlatformCapabilityActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Repository in owner/repo format")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "PlatformCapability member name to check (e.g. PrLifecycle)")]
    public Input<string> Capability { get; set; } = default!;

    [Input(Description = "Tenant id (GUID string) for driver resolution; empty = single-user/platform")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "The resolved platform kind (github/gitea/...), when the probe answered")]
    public Output<string?> PlatformKind { get; set; } = default!;

    [Output(Description = "True when the probe POSITIVELY answered unsupported (the Unsupported edge)")]
    public Output<bool> CapabilityUnsupported { get; set; } = default!;

    [JsonConstructor]
    public CheckPlatformCapabilityActivity() { }

    public CheckPlatformCapabilityActivity(
        ILogger<CheckPlatformCapabilityActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var repository = Repository.Get(context) ?? "";
        var capability = Capability.Get(context) ?? "";
        var tenantId = CreateBranchActivity.NormalizeTenant(TenantId.GetOrDefault(context));

        var apiClient = _apiClient ?? context.GetRequiredService<TammaApiClient>();
        GitCapabilitiesResponse? response = null;
        try
        {
            response = await apiClient
                .GetGitPlatformCapabilitiesAsync(repository, tenantId, context.CancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Capability probe threw for {Repository}/{Capability} — treating support as UNKNOWN (proceed)",
                repository, capability);
        }

        var supported = Evaluate(response, capability, out var platformKind);
        PlatformKind.Set(context, platformKind);
        CapabilityUnsupported.Set(context, !supported);

        if (supported)
        {
            await context.CompleteActivityWithOutcomesAsync("Supported");
        }
        else
        {
            _logger?.LogInformation(
                "Platform {PlatformKind} does not support {Capability} for {Repository} — routing "
                + "to the alternative step (§4)",
                platformKind ?? "?", capability, repository);
            await context.CompleteActivityWithOutcomesAsync("Unsupported");
        }
    }

    /// <summary>
    /// Pure decision core: only a SUCCESSFUL probe whose capability list
    /// positively lacks the capability answers unsupported. Unknown /
    /// unreachable / failed probes answer supported (see class remarks).
    /// </summary>
    public static bool Evaluate(GitCapabilitiesResponse? response, string capability, out string? platformKind)
    {
        platformKind = response?.PlatformKind;
        if (response is not { Success: true } || response.Capabilities is null)
        {
            return true; // unknown → proceed; the action's safety net decides
        }
        if (string.IsNullOrWhiteSpace(capability))
        {
            return true; // nothing to gate on — a misconfigured check must not skip actions
        }
        return response.Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
    }
}
