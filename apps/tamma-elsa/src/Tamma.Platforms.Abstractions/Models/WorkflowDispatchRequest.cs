namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Input shape for
/// <see cref="IGitPlatformActionsClient.DispatchWorkflowAsync"/>.
/// </summary>
/// <param name="Ref">
/// Branch / tag / SHA the workflow should run against.
/// </param>
/// <param name="WorkflowFileName">
/// Workflow filename (e.g. <c>tamma-agent.yml</c>) for platforms that
/// dispatch by file (GitHub Actions). May be null for platforms that
/// dispatch by name or by pipeline id (GitLab pipelines, Azure DevOps).
/// Drivers throw <see cref="PlatformError.InvalidRequest"/> with a
/// helpful code if a required field is missing for their platform.
/// </param>
/// <param name="Inputs">
/// String-keyed inputs (workflow_dispatch <c>inputs</c> in GitHub /
/// pipeline variables in GitLab). Empty dict is OK.
/// </param>
/// <param name="Variables">
/// Optional run-scoped CI variables (GitLab + Bitbucket). On GitHub
/// this is ignored — variables come from secrets / repo vars.
/// </param>
public sealed record WorkflowDispatchRequest(
    string Ref,
    string? WorkflowFileName,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string>? Variables = null);
