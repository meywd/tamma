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
/// non-route members = 22, matching the design's figure — plus Story 41-30's
/// <c>effect:schedule.create|update|delete</c> trio (the scheduled-trigger
/// admin mutations, which ride the ScheduleManage-gated /api/admin routes,
/// not EngineServiceOnly) = 25 — plus Story 44-2's ten NATIVE-tracker
/// mutations (<c>tracker.project.*</c>, <c>tracker.work-item.*</c>,
/// <c>tracker.preferences.*</c>, on the TrackerView/TrackerManage-gated
/// <c>/api</c> routes) = 35 — plus Story 43-8's four MENTORSHIP SESSION
/// LIFECYCLE mutations (<c>mentorship.session.*</c>, the repo's only
/// attribute-routed controller) = 39.
///
/// <para>
/// LIMITATION (43-2 D9, recorded not hidden): unlike <c>agent-action</c> /
/// <c>document-type</c>, this enum validates only against itself until Story
/// 43-8's route-table reflection harness lands — a new mutating route does not
/// fail any test authored in 43-2/43-3.
/// <b>Closed 2026-07-30</b> for the route-backed members: 21 of them now carry a
/// live <c>.Governs</c>/<c>[Governs]</c> binding whose descriptor <c>SiteKey</c>
/// is checked ordinally against the registered route pattern by
/// <c>GovernedEndpointBindingSweepTests</c>.
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
    /// enumerable. The C# site is the KB proxy route
    /// <c>POST /api/kb/mcp/tools/invoke</c> (<c>KbEndpoints.InvokeMcpTool</c>);
    /// the invocation it proxies executes inside the intelligence-server sidecar,
    /// so the C# side carries the governable call and no drift signal for the tool
    /// SET. Adding a server, or a tool on an existing server, changes nothing in
    /// the catalog — recorded, not closed.
    /// <para>Corrected 2026-07-29 (review F16): this doc and the descriptor SiteKey
    /// previously named <c>POST /api/kb/mcp/servers/{id}/start|stop</c>, which is
    /// not a route pattern — the alternation matches no registered route, making the
    /// member structurally unbindable by 43-8's ordinal SiteKey↔RawText check. The
    /// server start/stop pair is MCP-server LIFECYCLE, not tool invocation, and has
    /// no catalog member; giving it one is a vocabulary decision, not a repair.</para>
    /// </summary>
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

    // ── Story 41-30 — scheduled-trigger admin mutations (the seam's
    //    effect:schedule.* trio; the background actor itself is
    //    automation:tenant-scheduled-trigger-service) ──

    /// <summary><c>POST /api/admin/scheduled-triggers</c> —
    /// <c>ScheduledTriggerEndpoints.Create</c>. Creating a schedule arms a
    /// recurring per-tenant workflow dispatch (run-now shares this member —
    /// it claims a <c>manual:*</c> ledger window on an existing schedule).</summary>
    [Wire("schedule.create")] ScheduleCreate,

    /// <summary><c>PUT /api/admin/scheduled-triggers/{id}</c> —
    /// <c>ScheduledTriggerEndpoints.Update</c> (cron / target / enabled /
    /// input changes, including disable).</summary>
    [Wire("schedule.update")] ScheduleUpdate,

    /// <summary><c>DELETE /api/admin/scheduled-triggers/{id}</c> —
    /// <c>ScheduledTriggerEndpoints.Delete</c>. Deleting a schedule silently
    /// stops a tenant's recurring audit — audited via
    /// <c>SCHEDULE.TRIGGER.CHANGED</c>.</summary>
    [Wire("schedule.delete")] ScheduleDelete,

    // ── Story 44-2 — the NATIVE tracker's ten mutating routes
    //    (TrackerEndpoints). Distinct from git.issue.patch / jira.ticket.patch,
    //    which mutate an EXTERNAL tracker: these write Tamma's own
    //    tenant-schema system of record. All ten sit in the issue-tracking
    //    group (Story 44-2 AC10) and ship at AutonomyDial.Min — nothing gates
    //    them today, and the catalog is behaviour-preserving. ──

    /// <summary><c>POST /api/projects</c> — <c>TrackerEndpoints.CreateProject</c>.
    /// Mints a project and, with it, a FROZEN key prefix every future work-item
    /// key inherits.</summary>
    [Wire("tracker.project.create")] TrackerProjectCreate,

    /// <summary><c>PATCH /api/projects/{projectId}</c> —
    /// <c>TrackerEndpoints.PatchProject</c> (name / description / repository
    /// binding / estimate scale / archive state).</summary>
    [Wire("tracker.project.update")] TrackerProjectUpdate,

    /// <summary><c>DELETE /api/projects/{projectId}</c> —
    /// <c>TrackerEndpoints.DeleteProject</c>. Refused while the project holds
    /// work items (FK RESTRICT → 409), but an empty project's removal is not
    /// recoverable.</summary>
    [Wire("tracker.project.delete")] TrackerProjectDelete,

    /// <summary><c>POST /api/work-items</c> —
    /// <c>TrackerEndpoints.CreateWorkItem</c>. Consumes the project's number
    /// sequence, so a create is never a no-op even when later deleted.</summary>
    [Wire("tracker.work-item.create")] TrackerWorkItemCreate,

    /// <summary><c>PATCH /api/work-items/{id}</c> —
    /// <c>TrackerEndpoints.PatchWorkItem</c> (single-field tri-state patch).</summary>
    [Wire("tracker.work-item.update")] TrackerWorkItemUpdate,

    /// <summary><c>DELETE /api/work-items/{id}</c> —
    /// <c>TrackerEndpoints.DeleteWorkItem</c>. Refused while children exist
    /// (409 naming them); otherwise the row and its relation edges are
    /// gone.</summary>
    [Wire("tracker.work-item.delete")] TrackerWorkItemDelete,

    /// <summary><c>POST /api/work-items/{id}/assign</c> —
    /// <c>TrackerEndpoints.AssignWorkItem</c>. Catalogued separately from the
    /// generic patch because assignment is the axis 39-20's access model will
    /// eventually govern.</summary>
    [Wire("tracker.work-item.assign")] TrackerWorkItemAssign,

    /// <summary><c>POST /api/work-items/{id}/status</c> —
    /// <c>TrackerEndpoints.SetWorkItemStatus</c>. Separate member because a
    /// status move is the transition an admin would most plausibly want to gate
    /// independently of editing a title.</summary>
    [Wire("tracker.work-item.set-status")] TrackerWorkItemSetStatus,

    /// <summary><c>PUT /api/tracker/preferences</c> —
    /// <c>TrackerEndpoints.PutPreferences</c>. In SaaS this row is TENANT-wide
    /// tracker configuration, not a personal setting.</summary>
    [Wire("tracker.preferences.set")] TrackerPreferencesSet,

    /// <summary><c>DELETE /api/tracker/preferences</c> —
    /// <c>TrackerEndpoints.DeletePreferences</c> (falls back to the shipped
    /// defaults).</summary>
    [Wire("tracker.preferences.delete")] TrackerPreferencesDelete,

    // ── Story 43-8 (AC1 step 2, carve-out §A1 #1, closed 2026-07-30) — the
    //    MENTORSHIP SESSION LIFECYCLE. These four are the ONLY attribute-routed
    //    controller actions in the repo (MentorshipController), and they were
    //    baselined `no-catalog-member` when 43-8's harnesses landed because no
    //    member described them.
    //
    //    WHY THEY ARE CATALOGUED NOW, and why that is not "inventing a member to
    //    make a test green" (43-8 D10 forbids exactly that):
    //    POST /api/Mentorship/start does not merely write a row — it dispatches
    //    the Elsa `tamma-autonomous-mentorship` workflow
    //    (MentorshipController.cs:80 → ElsaWorkflowService.StartWorkflowAsync →
    //    POST /elsa/api/workflow-definitions/{name}/execute), i.e. it ARMS an
    //    autonomous, LLM-driven agent run across MentorshipWorkflow's 28 states.
    //    That is a real, executing capability, not a placeholder, and it is the
    //    same KIND of thing as effect:schedule.create (arms a recurring workflow
    //    dispatch) and effect:agent-dispatch.run (triggers an agent run) — both
    //    already catalogued, both reached from a UI rather than by the engine.
    //    Pause/resume/cancel are the controls over that in-flight run.
    //
    //    All four ship at AutonomyDial.Min (behaviour-preserving, epic decision
    //    D1): nothing gates them today and cataloguing them changes no behaviour.

    /// <summary><c>POST /api/Mentorship/start</c> —
    /// <c>MentorshipController.StartMentorship</c>. Mints a mentorship session AND
    /// dispatches the <c>tamma-autonomous-mentorship</c> Elsa workflow: after this
    /// completes, an autonomous agent run is under way.</summary>
    [Wire("mentorship.session.start")] MentorshipSessionStart,

    /// <summary><c>POST /api/Mentorship/sessions/{sessionId}/pause</c> —
    /// <c>MentorshipController.PauseSession</c>. Suspends the running mentorship
    /// workflow; <see cref="MentorshipSessionResume"/> restores it exactly.</summary>
    [Wire("mentorship.session.pause")] MentorshipSessionPause,

    /// <summary><c>POST /api/Mentorship/sessions/{sessionId}/resume</c> —
    /// <c>MentorshipController.ResumeSession</c>. Puts a paused mentorship
    /// workflow back into execution.</summary>
    [Wire("mentorship.session.resume")] MentorshipSessionResume,

    /// <summary><c>POST /api/Mentorship/sessions/{sessionId}/cancel</c> —
    /// <c>MentorshipController.CancelSession</c>. Terminates the mentorship
    /// workflow instance: the in-flight run is abandoned and cannot be
    /// resumed.</summary>
    [Wire("mentorship.session.cancel")] MentorshipSessionCancel,
}

/// <summary><see cref="ExternalEffect"/> wire helper.</summary>
public static class ExternalEffectExtensions
{
    /// <summary>The canonical wire string for <paramref name="effect"/>.</summary>
    public static string ToWire(this ExternalEffect effect) => EnumWire<ExternalEffect>.ToWire(effect);
}
