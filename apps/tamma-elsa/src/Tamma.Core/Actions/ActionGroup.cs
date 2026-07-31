using System.Collections.Frozen;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Core.Actions;

/// <summary>
/// The assignable-as-a-whole partition of the Action Catalog (Stories 43-2 D4 /
/// 43-3). SIXTEEN members — the epic README and design both NAMED sixteen while
/// asserting "15"; the resolution (43-3 C1/D2) is to ship 16, because merging two
/// semantically distinct groups to hit a round number is precisely the
/// wrong-but-consistent partition this vocabulary exists to avoid.
///
/// <para>
/// A STRICT PARTITION: every catalogued <see cref="ActionKey"/> belongs to
/// exactly one group (structural — <see cref="ActionDescriptor.Group"/> is a
/// non-nullable field), and no group may be empty
/// (<c>ACTION.CATALOG.GROUP_EMPTY</c> at static init). The by-group index is
/// PROJECTED from the descriptors (<c>ActionCatalog.ByGroup</c>, the
/// <c>RolePhaseMap</c> idiom) — never a hand-maintained second table, and no
/// <c>[Category]</c>-style attribute is introduced.
/// </para>
///
/// <para>
/// The partition rule (43-3 D1): group by KIND OF CONSEQUENCE WHEN THE ACTION
/// COMPLETES — not by role (impossible: shared tokens are reused across roles),
/// not by risk class (orthogonal, already <see cref="ActionRisk"/>), not by
/// producing agent.
/// </para>
///
/// <para>
/// These wires become PERSISTED VOCABULARY the moment Story 43-5's
/// <c>action_assignments</c> stores a group-scope row — renaming or merging a
/// group after that is a data migration.
/// </para>
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<ActionGroup>))]
public enum ActionGroup
{
    /// <summary>Investigation, triage, estimation, ordering — produces understanding, not a binding artifact.</summary>
    [Wire("planning-and-analysis")] PlanningAndAnalysis,

    /// <summary>Produces a binding artifact: code, a technical design, or an implementation plan others build against.</summary>
    [Wire("authoring")] Authoring,

    /// <summary>Review verdicts and acceptance decisions, including every document-type acceptance.</summary>
    [Wire("review-and-acceptance")] ReviewAndAcceptance,

    /// <summary>Human-readable prose about work already done; no binding technical content.</summary>
    [Wire("docs")] Docs,

    /// <summary>Reading workspace files and code search.</summary>
    [Wire("code-read")] CodeRead,

    /// <summary>Writing workspace files.</summary>
    [Wire("code-write")] CodeWrite,

    /// <summary>Executing arbitrary commands / spawning processes.</summary>
    [Wire("command-execution")] CommandExecution,

    /// <summary>Executing tests and CI runs (executing, not writing — test authorship is <see cref="Authoring"/>).</summary>
    [Wire("ci-and-test")] CiAndTest,

    /// <summary>Reading repository history and remotes.</summary>
    [Wire("source-control-read")] SourceControlRead,

    /// <summary>Writing to source control: commits, pushes, branches, pull requests, releases.</summary>
    [Wire("source-control-write")] SourceControlWrite,

    /// <summary>Mutating issues and tickets on tracker platforms.</summary>
    [Wire("issue-tracking")] IssueTracking,

    /// <summary>Deployment planning, CI/CD configuration, and production promotion/rollback.</summary>
    [Wire("deploy-control")] DeployControl,

    /// <summary>Outbound communication to humans: Slack, email.</summary>
    [Wire("external-comms")] ExternalComms,

    /// <summary>Invoking models, agents and MCP tools.</summary>
    [Wire("model-invocation")] ModelInvocation,

    /// <summary>Everything touching secret material — the SUBJECT dominates the verb here (43-3 D5.4).</summary>
    [Wire("secrets")] Secrets,

    /// <summary>Platform housekeeping: engine mediation writes, background actors, platform tasks.</summary>
    [Wire("platform-automation")] PlatformAutomation,
}

/// <summary><see cref="ActionGroup"/> wire helper + UI-facing descriptions.</summary>
public static class ActionGroupExtensions
{
    /// <summary>The canonical wire string for <paramref name="group"/>.</summary>
    public static string ToWire(this ActionGroup group) => EnumWire<ActionGroup>.ToWire(group);

    /// <summary>
    /// UI-facing description per group (Story 43-3 AC6/D8), rendered by the 43-7
    /// admin UI. Three of these are the ONLY honest disclosure of known holes
    /// (epic risk list) and are content-pinned by <c>ActionGroupDescriptionTests</c>:
    /// <c>deploy-control</c> (production deploy is an LLM tool loop),
    /// <c>command-execution</c> (shell can reach any governed route by curl),
    /// <c>model-invocation</c> (MCP is one coarse member). Do not trim them as
    /// "UI copy".
    /// </summary>
    public static readonly FrozenDictionary<ActionGroup, string> Descriptions =
        new Dictionary<ActionGroup, string>
        {
            [ActionGroup.PlanningAndAnalysis] =
                "Investigation, triage, estimation and ordering. These actions produce understanding — "
                + "findings, assessments, priorities — not artifacts other work builds against.",
            [ActionGroup.Authoring] =
                "Producing binding artifacts: code changes, technical designs, and the implementation plans "
                + "other work builds against. Includes writing test code, and includes infrastructure-as-code "
                + "authoring (implement-infrastructure) — gating deploy-control does NOT gate Terraform/IaC edits.",
            [ActionGroup.ReviewAndAcceptance] =
                "Review verdicts and acceptance decisions, including the acceptance decision for every "
                + "document type. Raising this group moves decisions from the orchestrator to people.",
            [ActionGroup.Docs] =
                "Human-readable prose about work already done — summaries, ADRs, release notes, runbooks. "
                + "No binding technical content.",
            [ActionGroup.CodeRead] =
                "Reading workspace files, searching code, and reading acceptance policy. Read-only inspection "
                + "of the working tree (repository history and remotes are source-control-read).",
            [ActionGroup.CodeWrite] =
                "Writing files in the workspace. Note file_write is a single undifferentiated member: there is "
                + "no per-path selector yet, so gating one directory means gating all writes.",
            [ActionGroup.CommandExecution] =
                "Executing shell commands and spawning processes. KNOWN BYPASS: shell_execute can reach any "
                + "governed HTTP route by curl and can perform any git operation directly — gating a "
                + "finer-grained action while leaving this group automated leaves that path open.",
            [ActionGroup.CiAndTest] =
                "Executing tests and triggering CI runs — executing, not writing; test authorship is in the "
                + "authoring group.",
            [ActionGroup.SourceControlRead] =
                "Reading repository history and remotes through the read-graded git subcommands.",
            [ActionGroup.SourceControlWrite] =
                "Writing to source control: commits and pushes via the write-graded git subcommands, plus "
                + "branch, pull-request and release operations on the git platform.",
            [ActionGroup.IssueTracking] =
                "Updating issues and tickets on the configured tracker platforms (git platform issues, Jira).",
            [ActionGroup.DeployControl] =
                "Deployment planning, CI/CD configuration, and production promotion/rollback. LIMITATION: "
                + "production deploy is an LLM tool loop, not a typed activity — gating the deploy effect gates "
                + "the stage transition, while the deploy commands themselves run inside the loop under "
                + "shell_execute. Infrastructure-as-code authoring lives in the authoring group.",
            [ActionGroup.ExternalComms] =
                "Outbound communication to humans: queued Slack messages and sent email. A sent message cannot "
                + "be unsent.",
            [ActionGroup.ModelInvocation] =
                "Invoking LLMs, dispatching agents, and invoking MCP tools. LIMITATION: MCP is one coarse "
                + "member with no per-server or per-tool granularity — adding an MCP server, or a tool on an "
                + "existing server, changes nothing in this catalog and nothing in CI, so no build check will "
                + "ever tell you a new MCP capability appeared. Because of that, MCP tool invocation REQUIRES "
                + "A PERSON by default; re-opening it re-opens EVERY server and EVERY tool on it.",
            [ActionGroup.Secrets] =
                "Everything touching secret material: the secrets audit action, secret rotation automation, and "
                + "the reveal effect. Secret reveal is informational-only and never enforceable — what governs "
                + "a secret is the action that needs it, not the read.",
            [ActionGroup.PlatformAutomation] =
                "Platform housekeeping: the engine's mediated persistence writes, the background sweepers and "
                + "schedulers, and queued platform tasks. Background actors cannot wait for a person, so "
                + "policy on them is two-state (automated or denied), never escalate-to-human.",
        }.ToFrozenDictionary();
}
