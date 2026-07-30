namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 (AC11, D7) — the RATCHET BASELINE for
/// <see cref="GovernedEndpointCoverageSweepTests"/>: every mutating endpoint of the
/// <c>Tamma.Api</c> host that carries no catalog binding today, each with a written
/// reason.
///
/// <para><b>This is a snapshot with a one-way valve, not an escape hatch.</b> Three
/// mechanisms, all asserted in <see cref="GovernedEndpointCoverageSweepTests"/>:</para>
/// <list type="number">
///   <item><b>Count pin</b> (<see cref="PinnedCount"/>) — an ADDITION fails the
///   build. This is the property that matters on day one: the backlog is large, but
///   it cannot GROW without a reviewer seeing a number change and asking "should
///   this new route be governed?".</item>
///   <item><b>Staleness</b> — an entry whose endpoint is now bound, or no longer
///   exists, fails until deleted. The baseline therefore drains as governance
///   lands, instead of rotting into a list nobody rereads.</item>
///   <item><b>Justification classification</b> — a placeholder ("TODO", "legacy",
///   "") cannot buy an entry.</item>
/// </list>
///
/// <para><b>Honesty about what this baseline means</b> (AC9). When 43-8's harnesses
/// first landed (2026-07-29) every in-scope endpoint was in here: ZERO routes were
/// bound. As of <b>2026-07-30</b> that is no longer true — 21 routes carry a real
/// binding (the 17 mediation routes plus <c>MentorshipController</c>'s four
/// <c>[HttpPost]</c> actions), so the numbers are <b>237 in scope, 216 baselined,
/// 21 bound</b>. The remaining honesty requirement is unchanged and is now
/// MACHINERY, not prose: <c>ActionEnforcementSites</c> computes the enforcement
/// sites per action from this same endpoint metadata and the admin API serialises
/// them as <c>enforcementSites</c>, so a catalog row with an EMPTY array must render
/// as "not enforced anywhere yet" rather than as governed. Note also that a binding
/// is metadata today: Story 43-9 attaches the filter that evaluates the gate.</para>
///
/// <para><b>Justifications are grouped by route family, deliberately.</b> Writing
/// ~230 individually-reasoned sentences would produce ~230 paraphrases of the same
/// four facts; grouping by family states the actual reason once and keeps the review
/// tractable. The classifier's floor is that every entry names a family and a
/// reason.</para>
///
/// <para><b>…but grouping must not hide a risk class</b> (review F15, 2026-07-29).
/// Family grouping is a readability device, not a licence to file a high-stakes route
/// behind a generic label. The <c>no-catalog-member: agent / workflow / document
/// orchestration write</c> paraphrase originally covered 30+ routes including the
/// agent-provider CREDENTIAL writes (<c>POST|DELETE
/// /api/v1/agents/providers/{provider}/credential</c>, <c>…/credential/rotate</c>) and
/// ESCALATION RESOLUTION (<c>POST /api/documents/escalations/{escalationId}/resolve</c>)
/// — none of which is the same risk class as a role-selection <c>PUT</c>. Those four
/// now carry their own justification lines. THE RULE: when a family contains a member
/// whose consequence differs in KIND from the rest, split it out, even though the
/// count pin does not change. A reviewer scanning this file must be able to see the
/// dangerous routes without reading the route patterns themselves.</para>
/// </summary>
internal static class KnownUngovernedEndpoints
{
    /// <summary>One baselined endpoint.</summary>
    /// <param name="Method">Upper-case HTTP method.</param>
    /// <param name="Pattern">Raw route pattern.</param>
    /// <param name="Justification">Why it is not governed yet; must classify.</param>
    internal sealed record Entry(string Method, string Pattern, string Justification);

    /// <summary>
    /// The classification vocabulary. An entry must read as one of these — the point
    /// is that a reader can tell, per entry, WHICH KIND of ungoverned it is, and
    /// therefore what would have to happen to govern it.
    /// </summary>
    internal static readonly string[] JustificationKeywords =
    [
        // Reached by a person through a UI, never by an autonomous agent. Governing
        // it would gate a human on themselves.
        "human-operated",

        // Auth, webhooks, health, diagnostics, billing callbacks — infrastructure
        // the platform needs to function; not an agent capability.
        "platform-infrastructure",

        // An engine mediation route that Story 43-9 will bind with .Governs plus the
        // enforcement filter. These are the ones that DO get governed next.
        "engine-mediation",

        // A catalogued capability whose route exists but whose binding is another
        // story's (named in the text).
        "binding-owned-by",

        // The gate-evaluation endpoint itself.
        //
        // PRE-PROVISIONED, ZERO USES as of 2026-07-29 (review F18(b)). The route it
        // classifies — POST /api/v1/governance/evaluate — does not exist yet; Story
        // 43-9 adds it and AC11 names this exact string as the justification it must
        // enter the baseline with. It is kept rather than deleted so 43-9's diff is
        // the route plus one baseline entry, not a vocabulary negotiation, and it is
        // NOT dead weight a reviewer must guess about: its use count is pinned at 0
        // by GovernedEndpointCoverageSweepTests.PreProvisionedJustificationKeyword_
        // isStillUnused, which goes red the day 43-9 uses it.
        "gate-evaluation-endpoint-cannot-gate-itself",

        // No catalog member exists for this capability at all; cataloguing it is a
        // governance decision nobody has taken yet (epic README open question 5).
        "no-catalog-member",
    ];

    /// <summary>Whether a justification reads as one of <see cref="JustificationKeywords"/>.</summary>
    internal static bool IsClassified(string justification) =>
        !string.IsNullOrWhiteSpace(justification)
        && JustificationKeywords.Any(k => justification.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The count pin (AC8(c)). SEEDED 2026-07-29 from the sweep itself. May only go
    /// DOWN, and every decrement must come with the deleted entry in the same diff.
    ///
    /// <para><b>237 → 216 (2026-07-30, Story 43-8 AC1 steps 2–3, carve-outs §A1 #1
    /// and #2).</b> The ratchet turning the right way for the first time: 21 entries
    /// were DELETED because their routes now carry a real binding — the 17 mediation
    /// routes bound with <c>.Governs</c> in <c>Program.cs</c>, plus
    /// <c>MentorshipController</c>'s four <c>[HttpPost]</c> actions bound with
    /// <c>[Governs]</c>. A route may never be both bound and baselined; the staleness
    /// arm of <c>EveryMutatingEndpoint_IsGovernedOrJustified</c> is what enforces
    /// that.</para>
    ///
    /// <para><b>The direction rule is ASSERTED, not only written</b> — the value is
    /// the last element of <see cref="PinHistory"/> and
    /// <c>GovernedEndpointCoverageSweepTests.TheRatchetPin_IsMechanicallyShrinkOnly</c>
    /// asserts that history is strictly decreasing (the
    /// <c>TemplateExampleConformanceTests</c> shape, adopted here for all four 43-8
    /// ratchets). Raising this pin now requires appending a value that makes the
    /// fixture RED.</para>
    /// </summary>
    internal const int PinnedCount = 216;

    /// <summary>
    /// The pin's recorded high-water history, oldest first; every element must be
    /// strictly LESS than its predecessor. 237 (seeded 2026-07-29, zero routes bound)
    /// → 216 (2026-07-30, 21 routes bound).
    ///
    /// <para><b>Honest residual</b> (same as the precedent's): this defeats the
    /// ordinary laundering path — editing one literal — but not deliberate tampering,
    /// since an author could append an increase AND edit the assertion. It moves the
    /// shrink-only property from prose into something a reviewer can see in a diff,
    /// which is the defect <c>ContractBindingTests.cs:255-271</c> has and this epic's
    /// ratchets must not inherit.</para>
    /// </summary>
    internal static readonly int[] PinHistory = [237, 216];

    /// <summary>
    /// The pinned size of the in-scope mutating surface (Correction 4: derive it at
    /// runtime, then pin it, rather than restating a literal from a grep).
    ///
    /// <para><b>UNCHANGED at 237 by the 2026-07-30 binding work, deliberately.</b> A
    /// bound route leaves the BASELINE but does not leave the in-scope SURFACE — it
    /// is still a mutating endpoint the sweep must see, it is simply governed now. So
    /// <see cref="PinnedCount"/> and this number are equal only while zero routes are
    /// bound (story 43-8 §A3 step 2); from the first binding onward they move
    /// independently and <c>InScopeEndpointCount_isPinned</c> /
    /// <c>Baseline_countIsPinned</c> must be reconciled separately. Today:
    /// 237 in scope, 216 baselined, 21 bound.</para>
    /// </summary>
    internal const int PinnedInScopeCount = 237;

    /// <summary>The baseline itself.</summary>
    internal static readonly IReadOnlyList<Entry> All =
    [
        new("*", "/health",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("*", "/health/live",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("*", "/health/ready",
            "platform-infrastructure: a health-check endpoint that declares no HTTP method (so it accepts POST) and performs no application effect"),
        new("DELETE", "/api/acceptance-rules/{documentTypeKey}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/actions/policy/actions/{ns}/{key}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/actions/policy/groups/{group}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/admin/actions/ceiling/actions/{ns}/{key}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/actions/ceiling/groups/{group}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/api-keys/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/conventions/{role}/{action}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/pricing/plans/{slug}/versions/{version:int}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/providers/{key}/settings",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/scheduled-triggers/{id:guid}",
            "binding-owned-by Story 43-9: catalogued as effect:schedule.create|update|delete (Story 41-30); the route exists, the binding does not yet"),
        new("DELETE", "/api/admin/service-keys/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/tenant-databases/{databaseId:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/invites/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/{id}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/admin/users/{id}/keys/{keyId}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/agents/{agentId:guid}/enablement",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/conventions/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/engine/issue-labels/{repo}/{issueNumber}/{label}",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("DELETE", "/api/kb/index",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("DELETE", "/api/kb/vector-db/delete",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("DELETE", "/api/pricing/providers/{provider}/byok",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/projects/{projectId:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/prompts/system/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/prompts/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/providers/providers/{handle}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/tracker/preferences",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/v1/admin/alert-channels/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/v1/admin/alert-rules/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("DELETE", "/api/v1/agents/providers/{provider}/credential",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — this route and the two POSTs below store, rotate and delete a provider secret for the tenant's agents. Split onto its own justification line 2026-07-29 (review F15): it was hidden inside the generic 'agent / workflow / document orchestration write' family, and a credential write is not the same risk class as a role-selection PUT. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("DELETE", "/api/v1/agents/providers/{provider}/model",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("DELETE", "/api/v1/integrations/email/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/v1/integrations/jira/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/alert-channels/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/api-keys/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/invites/{inviteId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/v1/orgs/{tenantId:guid}/members/{userId:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("DELETE", "/api/work-items/{id:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("DELETE", "/api/workflows/instances/{id}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("GET", "/api/v1/secrets/reveal/{token}",
            "engine-mediation: catalogued as effect:secret.reveal and deliberately NEVER enforceable (Enforceable=false) — the reveal is how an already-authorized action fetches its credential"),
        new("PATCH", "/api/admin/providers/{key}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/admin/tenant-databases/{databaseId:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/admin/tenants/{tenantId:guid}/plan",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/projects/{projectId:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("PATCH", "/api/v1/admin/alert-channels/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/v1/admin/alert-rules/{id:guid}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PATCH", "/api/v1/onboarding/repos/{installationId:long}/{repoId:long}",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("PATCH", "/api/v1/orgs/{tenantId:guid}/alert-channels/{id:guid}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PATCH", "/api/work-items/{id:guid}",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/actions/policy/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/adl/blocker/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/clarify/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/deploy-approval/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/design/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/adl/merge-approval/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/admin/api-keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/conventions/{role}/{action}/reset",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/kek/rotate/retry",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/kek/rotate/start",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pools/{tenantId:guid}/evict",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pricing/plans",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/pricing/plans/custom",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/providers",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/scheduled-triggers/",
            "binding-owned-by Story 43-9: catalogued as effect:schedule.create|update|delete (Story 41-30); the route exists, the binding does not yet"),
        new("POST", "/api/admin/scheduled-triggers/{id:guid}/run-now",
            "binding-owned-by Story 43-9: catalogued as effect:schedule.create|update|delete (Story 41-30); the route exists, the binding does not yet"),
        new("POST", "/api/admin/secrets",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/secrets/{id:guid}/retire-version/{versionNumber:int}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/secrets/{id:guid}/rotate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/service-keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/service-keys/{id}/rotate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenant-databases",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/migrate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/cancel-delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/force-delete",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/actions/retry",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/cleanup",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/deprovision",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/impersonate",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/move",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/plan/cancel",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/tenants/{tenantId:guid}/provision",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/users/invite",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/admin/users/{id}/keys",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/agents/",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/archive",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/rollback",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/agents/{id:guid}/versions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/auth/impersonate/end",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/auth/logout",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/config/sanitize",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/conventions/resolve",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/documents/decisions/{sessionId}/resume",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/documents/escalations/{escalationId}/resolve",
            "no-catalog-member: ESCALATION RESOLUTION — closes an escalated document review and releases the suspended lifecycle. Split onto its own justification line 2026-07-29 (review F15): resolving an escalation is the very act the escalation ring exists to require a decision for, so grouping it with the generic 'agent / workflow / document orchestration write' family hid the highest-stakes route in that family. Cataloguing it is epic README open question 5"),
        new("POST", "/api/engine/command",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/create-issue",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/cycle-result",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/execute-task",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/issue-comment",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/issue-labels",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/query-context",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/store-context",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/engine/trigger-ci",
            "no-catalog-member: an ENGINE ORCHESTRATION CALLBACK on the /api/engine group (WorkflowsManage, not EngineServiceOnly) with no catalog member of its own. Justification corrected 2026-07-30 when Story 43-8 AC1 step 3 landed the real bindings: these entries previously read 'an EngineServiceOnly route … its catalogued effect:* member exists and Story 43-9 binds it', which was a family paraphrase and is now provably false — all 17 route-backed effect:* members ARE bound, and none of them names this route. Cataloguing this family is epic README open question 5"),
        new("POST", "/api/github/webhooks",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/kb/context/feedback",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/index/trigger",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        // WORDING CORRECTED 2026-07-29 (adversarial review F16). These two entries
        // previously claimed the start/stop pair was "the C# half of the catalogued
        // effect:mcp.tool.invoke member". That was never true in a bindable sense:
        // the member's SiteKey read "POST /api/kb/mcp/servers/{id}/start|stop", an
        // ALTERNATION, and GovernedEndpointBindingSweepTests compares a SiteKey's
        // route part to a single $"{method} {RawText}" ordinally — so it matched no
        // real route and neither start nor stop could ever have been bound to it.
        // Server start/stop is MCP-server LIFECYCLE, not tool invocation; it has no
        // catalog member of its own, and giving it one is a vocabulary decision.
        new("POST", "/api/kb/mcp/servers/{id}/start",
            "no-catalog-member: MCP-SERVER LIFECYCLE — starting a configured MCP server changes which tools exist for the model to call. It is NOT effect:mcp.tool.invoke (that member names the invocation route) and no catalog member covers server lifecycle; adding one is a vocabulary decision nobody has taken. Note that this route's real consequence — the tool SET changing — has no drift signal anywhere in this epic"),
        new("POST", "/api/kb/mcp/servers/{id}/stop",
            "no-catalog-member: MCP-SERVER LIFECYCLE — the stop half of the pair above; same absent catalog member, same absent drift signal"),
        new("POST", "/api/kb/mcp/tools/invoke",
            "binding-owned-by Story 43-9: the direct MCP tool-invocation proxy and the ONE route effect:mcp.tool.invoke actually names. 43-9 attaches the .Governs binding plus the enforcement filter"),
        new("POST", "/api/kb/rag/query",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/vector-db/search",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/kb/vector-db/upsert",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("POST", "/api/onboarding/install",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/pricing/providers/{provider}/byok",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/pricing/subscribe",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/projects",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/prompts/{role}/{action}/render",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/prompts/{role}/{action}/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/chain/resolve",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/diagnostics",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/diagnostics/batch",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/failure",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/reset",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/health/providers/{key}/success",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/providers/create",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/providers/providers/{handle}/execute",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/admin/alert-channels",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alert-rules",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alert-rules/{id:guid}/_test",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/_test",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/{id:guid}/acknowledge",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/alerts/{id:guid}/resolve",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/admin/audit/checkpoint",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("POST", "/api/v1/agents/",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/config/validate",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/providers/{provider}/credential",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — stores a provider secret for the tenant's agents. Split onto its own justification line 2026-07-29 (review F15) so a reviewer scanning this baseline can SEE it instead of reading a generic 'agent / workflow / document orchestration write' paraphrase that also covers role-selection PUTs. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("POST", "/api/v1/agents/providers/{provider}/credential/rotate",
            "no-catalog-member: AGENT-PROVIDER CREDENTIAL WRITE — rotates a stored provider secret. Split onto its own justification line 2026-07-29 (review F15); see the credential POST above. Cataloguing it is epic README open question 5, and this sub-family should be taken FIRST"),
        new("POST", "/api/v1/agents/resolve-for-phase",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/{id:guid}/archive",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/agents/{id:guid}/versions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/auth/login",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/password-reset/confirm",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/password-reset/request",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/refresh",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/register",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/resend-verification",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/switch-org",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/auth/verify-email",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/installations/{id}/rotate-key",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/integrations/email/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/integrations/jira/credential",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("POST", "/api/v1/llm/chat",
            "no-catalog-member: the SaaS API-key LLM chat route (SaaSEndpoints.LlmChat), a separate surface from the engine mediation seam POST /api/v1/llm/call that effect:llm.call names and is now bound to. Justification corrected 2026-07-30 (43-8 AC1 step 3): it previously claimed a catalogued member exists for it, which was a family paraphrase and is false. Cataloguing it is epic README open question 5"),
        new("POST", "/api/v1/onboarding/complete",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/v1/orgs/",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/invites/accept",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alert-channels",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alerts/{id:guid}/acknowledge",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/alerts/{id:guid}/resolve",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/api-keys",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/invites",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/invites/{inviteId:guid}/resend",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/reprovision",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/retire-version/{versionNumber:int}",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/rotate",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/secrets/{id:guid}/rotate-workflow",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/orgs/{tenantId:guid}/transfer-ownership",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("POST", "/api/v1/secrets/{secretId:guid}/rotate",
            "human-operated: the secret cabinet's admin surface, driven by a person through the dashboard; no catalog member covers cabinet CRUD"),
        new("POST", "/api/v1/workflows/{id}/result",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/v1/workflows/{id}/status",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/webhooks/{platform}",
            "platform-infrastructure: authentication, platform webhooks and app installation callbacks — the platform's own plumbing, not an autonomous agent capability"),
        new("POST", "/api/work-items",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/work-items/{id:guid}/assign",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/work-items/{id:guid}/status",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("POST", "/api/workflows/definitions",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/workflows/instances",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("POST", "/api/workflows/instances/{id}/cancel",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/acceptance-rules/{documentTypeKey}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/enabled",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/enforce",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/roles",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/actions/{ns}/{key}/threshold",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/actions/policy/groups/{group}/threshold",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/admin/actions/ceiling/actions/{ns}/{key}/threshold",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/actions/ceiling/groups/{group}/threshold",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/conventions/{role}/{action}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/pricing/margins",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/pricing/plans/{slug}",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/providers/{key}/prices",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/providers/{key}/settings",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/scheduled-triggers/{id:guid}",
            "binding-owned-by Story 43-9: catalogued as effect:schedule.create|update|delete (Story 41-30); the route exists, the binding does not yet"),
        new("PUT", "/api/admin/tenants/{tenantId:guid}/plan",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/admin/users/{id}/role",
            "human-operated: platform-owner admin console mutation behind PlatformOwnerAccess; reached by a person, never by an agent, so gating it would gate a human on themselves"),
        new("PUT", "/api/agents/role-selections/{role}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/agents/{agentId:guid}/enablement",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/config/agents",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/prompts/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/providers",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/sanitize/rules",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/config/security",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/conventions/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/kb/index/config",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("PUT", "/api/kb/mcp/config",
            "no-catalog-member: MCP server configuration write — adding a server changes which tools exist, and that has NO drift signal anywhere in this epic (effect:mcp.tool.invoke is one coarse member by construction)"),
        new("PUT", "/api/kb/rag/config",
            "no-catalog-member: knowledge-base / RAG proxy write; the sidecar surface past the proxy is ungoverned and no catalog member covers it"),
        new("PUT", "/api/prompts/system/{role}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/prompts/{role}/{action}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/providers/diagnostics/budget/{accountId}",
            "human-operated: tenant configuration surface (pricing, prompts, conventions, providers, integrations, autonomy policy) edited by a person in the dashboard"),
        new("PUT", "/api/tracker/preferences",
            "binding-owned-by Story 44-2: catalogued as an effect:tracker.* member; the native tracker's routes ship with descriptors but no .Governs binding yet"),
        new("PUT", "/api/v1/agents/config",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/v1/agents/providers/{provider}/model",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
        new("PUT", "/api/v1/orgs/{tenantId:guid}/members/{userId:guid}/role",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PUT", "/api/v1/orgs/{tenantId:guid}/settings",
            "human-operated: organisation / membership / invite management driven by a tenant admin through the dashboard"),
        new("PUT", "/api/workflows/instances/{id}",
            "no-catalog-member: agent / workflow / document orchestration write with no catalog member; classifying this family is epic README open question 5"),
    ];

    /// <summary>Indexed by <c>"{METHOD} {pattern}"</c>.</summary>
    internal static readonly IReadOnlyDictionary<string, Entry> BySiteKey =
        All.GroupBy(e => $"{e.Method} {e.Pattern}", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
}
