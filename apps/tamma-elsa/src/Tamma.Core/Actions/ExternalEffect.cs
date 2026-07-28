using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The <c>effect:*</c> plane of the Action Catalog (Story 43-2 AC5): every
/// consequential side effect the engine reaches through the mediation seam, plus
/// the four surfaces no route sweep can see (process spawn, MCP, secret reveal)
/// and the two deploy stage transitions. Re-derived from the tree on 2026-07-27:
/// <c>grep 'RequireAuthorization("EngineServiceOnly")' Tamma.Api/Program.cs</c>
/// finds 26 routes of which 17 are MUTATING (the engine group's 5 writes + 12
/// app-level writes); the other 9 are GETs and are not catalogued. 17 + 5
/// non-route members = 22, matching the design's figure.
///
/// <para>
/// LIMITATION (43-2 D9, recorded not hidden): unlike <c>agent-action</c> /
/// <c>document-type</c>, this enum validates only against itself until Story
/// 43-8's route-table reflection harness lands — a new mutating route does not
/// fail any test authored in 43-2/43-3.
/// </para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ExternalEffect>))]
public enum ExternalEffect
{
    // ── The engine mediation group's five writes (POST /api/engine/…) ──

    /// <summary><c>POST /api/engine/events</c> — <c>EngineEndpoints.AppendEvents</c> (DCB event append).</summary>
    [Wire("engine.events.append")] EngineEventsAppend,

    /// <summary><c>POST /api/engine/platform-events</c> — <c>EngineEndpoints.AppendPlatformEvents</c>.</summary>
    [Wire("engine.platform-events.append")] EnginePlatformEventsAppend,

    /// <summary><c>POST /api/engine/documents</c> — <c>DocumentEndpoints.PersistFromEngine</c>.</summary>
    [Wire("engine.document.persist")] EngineDocumentPersist,

    /// <summary><c>POST /api/engine/documents/{documentId}/status</c> — <c>DocumentEndpoints.SetStatusFromEngine</c>.</summary>
    [Wire("engine.document.set-status")] EngineDocumentSetStatus,

    /// <summary><c>POST /api/engine/channel/outbox</c> — <c>ChannelEndpoints.EnqueueFromEngine</c>.</summary>
    [Wire("engine.channel-outbox.enqueue")] EngineChannelOutboxEnqueue,

    // ── App-level mutating EngineServiceOnly routes ──

    /// <summary><c>POST /api/v1/llm/call</c> — <c>LlmCallEndpoints.CallLlm</c>. Seam A is observe-only PERMANENTLY (epic decision D1).</summary>
    [Wire("llm.call")] LlmCall,

    /// <summary><c>POST /api/v1/git/{owner}/{repo}/branches</c> — <c>GitEndpoints.CreateBranch</c>.</summary>
    [Wire("git.branch.create")] GitBranchCreate,

    /// <summary><c>DELETE /api/v1/git/{owner}/{repo}/branches</c> — <c>GitEndpoints.DeleteBranch</c>.</summary>
    [Wire("git.branch.delete")] GitBranchDelete,

    /// <summary><c>POST /api/v1/git/{owner}/{repo}/pull-requests</c> — <c>GitEndpoints.CreatePullRequest</c>.</summary>
    [Wire("git.pull-request.create")] GitPullRequestCreate,

    /// <summary><c>PUT /api/v1/git/{owner}/{repo}/pull-requests/{n}/merge</c> — <c>GitEndpoints.MergePullRequest</c>.</summary>
    [Wire("git.pull-request.merge")] GitPullRequestMerge,

    /// <summary><c>POST /api/v1/git/{owner}/{repo}/releases</c> — <c>GitEndpoints.CreateRelease</c>.</summary>
    [Wire("git.release.create")] GitReleaseCreate,

    /// <summary><c>PATCH /api/v1/git/{owner}/{repo}/issues/{n}</c> — <c>GitEndpoints.UpdateIssue</c>.</summary>
    [Wire("git.issue.patch")] GitIssuePatch,

    /// <summary><c>PATCH /api/v1/jira/tickets/{ticketId}</c> — <c>JiraEndpoints.UpdateTicket</c>.</summary>
    [Wire("jira.ticket.patch")] JiraTicketPatch,

    /// <summary><c>POST /api/v1/ci/{owner}/{repo}/test-runs</c> — <c>CiEndpoints.TriggerTests</c>.</summary>
    [Wire("ci.tests.trigger")] CiTestsTrigger,

    /// <summary><c>POST /api/v1/agent-dispatch/{owner}/{repo}/runs</c> — <c>AgentDispatchEndpoints.TriggerRun</c>.</summary>
    [Wire("agent-dispatch.run")] AgentDispatchRun,

    /// <summary><c>POST /api/v1/notifications/slack</c> — <c>NotificationEndpoints.QueueSlack</c>.</summary>
    [Wire("notify.slack.queue")] NotifySlackQueue,

    /// <summary><c>POST /api/v1/notifications/email</c> — <c>EmailEndpoints.SendEmail</c>.</summary>
    [Wire("notify.email.send")] NotifyEmailSend,

    // ── Surfaces no route sweep can see ──

    /// <summary>MCP tool invocation — ONE COARSE MEMBER, deliberately (epic risk
    /// list): server and tool names arrive in request bodies and are not
    /// enumerable. C# surface today is the KB proxy
    /// (<c>POST /api/kb/mcp/servers/{id}/start|stop</c>, <c>KbEndpoints</c>);
    /// invocation itself happens inside the intelligence-server sidecar. Adding a
    /// server, or a tool on an existing server, changes nothing in the catalog —
    /// recorded, not closed.</summary>
    [Wire("mcp.tool.invoke")] McpToolInvoke,

    /// <summary><c>GET /api/v1/secrets/reveal/{token}</c> —
    /// <c>SecretEndpoints.RevealSecret</c>. INFORMATIONAL ONLY, NEVER ENFORCEABLE
    /// (epic README open question 2, ANSWERED 2026-07-25): reading a secret never
    /// requires a human — the reveal is how an already-authorized action gets its
    /// credential, and it can fire many times inside one run. What governs a
    /// secret is the action that needs it. <c>ActionDescriptor.Enforceable</c> is
    /// <c>false</c> for this member alone.</summary>
    [Wire("secret.reveal")] SecretReveal,

    /// <summary>OS process spawn inside the tool loop —
    /// <c>ShellExecuteTool</c>'s <c>ProcessStartInfo</c> (also reached by
    /// <c>GitOperationsTool</c>/<c>RunTestsTool</c>, which are separately
    /// catalogued as tools).</summary>
    [Wire("process.spawn")] ProcessSpawn,

    /// <summary>Production promotion stage transition in
    /// <c>Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow</c>. NOTE (epic
    /// risk 8): production deploy is an LLM tool loop, not a typed activity —
    /// gating this effect gates the STAGE TRANSITION; the deploy itself happens
    /// inside the loop under <c>tool:shell_execute</c>. This limitation must
    /// surface in the <c>deploy-control</c> group description, not only here.</summary>
    [Wire("deploy.promote-prod")] DeployPromoteProd,

    /// <summary>Production rollback branch (<c>RollbackProduction</c>) in
    /// <c>Tamma.ElsaServer/Workflows/DeploymentPipelineWorkflow</c>. Same
    /// LLM-tool-loop limitation as <see cref="DeployPromoteProd"/>.</summary>
    [Wire("deploy.rollback")] DeployRollback,
}

/// <summary><see cref="ExternalEffect"/> wire helper.</summary>
public static class ExternalEffectExtensions
{
    /// <summary>The canonical wire string for <paramref name="effect"/>.</summary>
    public static string ToWire(this ExternalEffect effect) => EnumWire<ExternalEffect>.ToWire(effect);
}
