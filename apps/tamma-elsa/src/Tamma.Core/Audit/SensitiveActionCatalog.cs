namespace Tamma.Core.Audit;

/// <summary>
/// Story 37-1 — the canonical catalog of compliance-relevant sensitive
/// actions. Shape mirrors <c>SecretAuditEventTypes</c> (a const-class of
/// canonical event-type strings) plus an immutable <see cref="ByCode"/>
/// lookup that classifies each into a category / severity / SOC2 control.
///
/// <para><b>This catalog MAPS already-emitted DCB events; it does NOT
/// re-emit them.</b> The <see cref="AuditProjector"/> (Story 37-1) observes
/// the events already flowing into <c>domain_events</c> / <c>platform_events</c>
/// and materialises a curated <c>audit_records</c> row for each catalogued
/// match. Adding a code here does not add an emit call-site.</para>
///
/// <para>Codes flagged <see cref="SensitiveActionDescriptor.MapsExistingEmitter"/>
/// <c>= true</c> were verified by grep at authoring (2026-06-17) against the
/// real emitters: <c>SecretAuditEventTypes</c> (Epic 29 secret cabinet),
/// <c>AdminImpersonationsEndpoints</c> (28-R2 impersonation platform events),
/// <c>OrgEndpoints</c> (tenant membership/lifecycle), <c>AuthEndpoints</c>
/// (logout-all / org-switch / refresh-reuse platform events),
/// <c>ConventionEventsService</c> / <c>PromptEventsService</c> (config edits),
/// <c>AgentRepository</c> / <c>AgentEndpoints</c> (agent entity + config),
/// <c>BillingEvents</c> (billing), and the Story 4-x autonomous-loop emitters.
/// Codes flagged <c>false</c> are forward-looking taxonomy entries (the
/// catalog is the future taxonomy too) for sensitive actions Tamma does not
/// yet emit; the projector will start materialising them the day an emitter
/// lands, with zero catalog change.</para>
/// </summary>
public static class SensitiveActionCatalog
{
    // ── SECRET (maps existing Epic 29 SecretAuditEventTypes — NOT re-emitted) ──
    public const string SecretRead = "SECRET.READ";
    public const string SecretWrite = "SECRET.WRITE";
    public const string SecretReveal = "SECRET.REVEAL";
    public const string SecretRotateStarted = "SECRET.ROTATE.STARTED";
    public const string SecretRotateSucceeded = "SECRET.ROTATE.SUCCESS";
    public const string SecretRotateFailed = "SECRET.ROTATE.FAILED";
    public const string SecretVersionRevoked = "SECRET.VERSION.REVOKED";

    // ── RBAC (maps existing OrgEndpoints emitters) ──
    public const string TenantMemberRoleChanged = "TENANT.MEMBER_ROLE_CHANGED.SUCCESS";
    public const string TenantMemberInvited = "TENANT.MEMBER_INVITED.SUCCESS";
    public const string TenantMemberJoined = "TENANT.MEMBER_JOINED.SUCCESS";
    public const string TenantMemberRemoved = "TENANT.MEMBER_REMOVED.SUCCESS";
    public const string TenantOwnershipTransferred = "TENANT.OWNERSHIP_TRANSFERRED.SUCCESS";

    /// <summary>Forward-looking: a platform-admin changing a user's platform role
    /// (AdminEndpoints logs <c>USER.ROLE_CHANGED.SUCCESS</c> today but does NOT
    /// append it as a DCB event — when it starts emitting, this catalogs it).</summary>
    public const string UserRoleChanged = "USER.ROLE_CHANGED.SUCCESS";

    // ── IMPERSONATION (maps existing 28-R2 platform-event emitters) ──
    public const string ImpersonationStarted = "IMPERSONATION.STARTED";
    public const string ImpersonationEnded = "IMPERSONATION.ENDED";

    // ── CONFIG (maps existing convention/prompt/agent-config emitters) ──
    public const string ConventionCreated = "CONVENTION.CREATED.SUCCESS";
    public const string ConventionUpdated = "CONVENTION.UPDATED.SUCCESS";
    public const string ConventionDeleted = "CONVENTION.DELETED.SUCCESS";
    public const string ConventionReset = "CONVENTION.RESET.SUCCESS";
    public const string AgentConfigUpdated = "AGENT_CONFIG.UPDATED.SUCCESS";

    /// <summary>Story 46-1 (AC8) — a provider_settings mutation: platform
    /// default model set/removed, provider enabled/disabled, or a tenant/user
    /// model override set/removed. Wired by <c>ProviderAdminEndpoints</c> +
    /// the tenant model routes in <c>ProviderCredentialEndpoints</c>'s surface.
    /// The concrete operation travels in the event's <c>operation</c> tag
    /// (set|removed|enabled|disabled) with <c>scope</c> platform|tenant|user;
    /// data carries previous→new model. Never any key material — this is a
    /// configuration change, not a credential one (hence Config/CC8.1, not
    /// Byok — the BYOK category is for key custody).</summary>
    public const string ProviderSettingsChanged = "PROVIDER.SETTINGS_CHANGED.SUCCESS";

    /// <summary>Forward-looking: an edit to the tenant content-sanitization ruleset.</summary>
    public const string SanitizationRuleChanged = "SANITIZATION_RULE.CHANGED.SUCCESS";

    // ── PERSONA (maps existing prompt-store emitters; system prompts are personas) ──
    public const string PromptCreated = "PROMPT.CREATED.SUCCESS";
    public const string PromptUpdated = "PROMPT.UPDATED.SUCCESS";
    public const string PromptDeleted = "PROMPT.DELETED.SUCCESS";
    public const string PromptReset = "PROMPT.RESET.SUCCESS";

    // ── BYOK (provider-key wired in 37-10; provider-chain forward-looking) ──
    /// <summary>A tenant BYOK provider key was set / rotated / removed — wired by
    /// Story 37-10 (<c>ProviderCredentialEndpoints</c>). The concrete operation
    /// (set|rotated|removed) travels in the event's <c>operation</c> tag/data;
    /// the underlying <c>SECRET.*</c> cabinet write stays the secret source of
    /// truth (this is the curated, catalog-facing BYOK event, not a second write).</summary>
    public const string ProviderKeyChanged = "PROVIDER_KEY.CHANGED.SUCCESS";
    public const string ProviderChainChanged = "PROVIDER_CHAIN.CHANGED.SUCCESS";

    /// <summary>A tenant BYOK integration credential (JIRA / email) was set or
    /// removed — wired by <c>IntegrationCredentialEndpoints</c>. The concrete
    /// operation (set|removed) + integration (jira|email) travel in the event's
    /// <c>operation</c>/<c>integration</c> tag/data; the underlying <c>SECRET.*</c>
    /// cabinet write stays the secret source of truth (this is the curated,
    /// catalog-facing BYOK event, not a second write).</summary>
    public const string IntegrationCredentialChanged = "INTEGRATION_CREDENTIAL.CHANGED.SUCCESS";

    // ── BILLING (maps existing BillingEvents; budget edits forward-looking) ──
    public const string BillingCustomerCreated = "BILLING.CUSTOMER.CREATED";
    public const string PlanUpdated = "PLAN.UPDATED";
    public const string PlanVersionCreated = "PLAN.VERSION.CREATED";

    /// <summary>Forward-looking: a tenant changing its spend budget config.</summary>
    public const string BudgetChanged = "BUDGET.CONFIG.CHANGED.SUCCESS";

    // ── EXPORT (forward-looking — Story 37-10/37-11 data export + GDPR DSAR) ──
    public const string DataExported = "DATA.EXPORTED.SUCCESS";
    public const string DsarRequested = "GDPR.DSAR.REQUESTED";

    // ── AUTH (maps existing AuthEndpoints emitters; login/refresh/api-key wired in 37-10) ──
    public const string LogoutAll = "USER.LOGOUT_ALL.SUCCESS";
    public const string OrgSwitched = "USER.ORG_SWITCHED.SUCCESS";
    public const string RefreshReuseDetected = "AUTH.REFRESH_REUSE_DETECTED";

    /// <summary>An interactive login succeeded — wired by Story 37-10
    /// (<c>AuthEndpoints.Login</c>) as a platform-edge auth event.</summary>
    public const string LoginSuccess = "AUTH.LOGIN.SUCCESS";

    /// <summary>An interactive login failed (brute-force signal). Wired by Story
    /// 37-10 (<c>AuthEndpoints.Login</c>) carrying a machine-readable reason.</summary>
    public const string LoginFailure = "AUTH.LOGIN.FAILURE";

    /// <summary>A refresh-token rotation succeeded — wired by Story 37-10
    /// (<c>AuthEndpoints.Refresh</c>). Distinct from the reuse-detection event.</summary>
    public const string TokenRefreshed = "AUTH.TOKEN.REFRESHED";

    /// <summary>An API key authenticated a request — wired by Story 37-10
    /// (<c>ApiKeyAuthHandler</c>), throttled to a heartbeat (never per-request).</summary>
    public const string ApiKeyUsed = "AUTH.APIKEY.USED";

    /// <summary>Forward-looking: a password-reset completed.</summary>
    public const string PasswordReset = "AUTH.PASSWORD_RESET.SUCCESS";

    // ── TENANT lifecycle (maps existing OrgEndpoints / provisioning emitters) ──
    public const string TenantCreated = "TENANT.CREATED.SUCCESS";
    public const string TenantProvisioned = "TENANT.PROVISIONED.SUCCESS";
    public const string TenantDeleted = "TENANT.DELETED.SUCCESS";
    public const string TenantPurged = "TENANT.PURGED.SUCCESS";
    public const string TenantMoveRequested = "TENANT.MOVE.REQUESTED";

    // ── AGENT (maps existing AgentRepository / AgentEndpoints / loop emitters) ──
    public const string AgentCreated = "AGENT.CREATED.SUCCESS";
    public const string AgentArchived = "AGENT.ARCHIVED.SUCCESS";
    public const string AgentVersionPublished = "AGENT.VERSION_PUBLISHED.SUCCESS";
    public const string AgentDispatchSucceeded = "AGENT.DISPATCH.SUCCESS";
    public const string AgentDispatchFailed = "AGENT.DISPATCH.FAILED";
    public const string CodeGeneratedFailed = "CODE.GENERATED.FAILED";

    /// <summary>
    /// Immutable code → descriptor lookup. The single source of truth for
    /// "is this raw event a sensitive action, and how is it classified". The
    /// projector keys exclusively off this dictionary; nothing else decides
    /// what is auditable.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SensitiveActionDescriptor> ByCode;

    /// <summary>
    /// True when the projector should materialise an <c>audit_record</c> for a
    /// raw event of this type. O(1) dictionary containment check.
    /// </summary>
    public static bool IsSensitive(string? eventType) =>
        !string.IsNullOrEmpty(eventType) && ByCode.ContainsKey(eventType);

    /// <summary>Resolve the descriptor for a raw event type, or <c>null</c> if not sensitive.</summary>
    public static SensitiveActionDescriptor? Resolve(string? eventType) =>
        !string.IsNullOrEmpty(eventType) && ByCode.TryGetValue(eventType, out var d) ? d : null;

    static SensitiveActionCatalog()
    {
        // Local builder helper keeps each entry to one terse line and pins
        // the ActionCode to the dictionary key (no drift).
        var map = new Dictionary<string, SensitiveActionDescriptor>(StringComparer.Ordinal);

        void Add(string code, AuditCategory cat, AuditSeverity sev, string soc2,
            string targetHint, bool mapsExisting) =>
            map[code] = new SensitiveActionDescriptor(code, cat, sev, soc2, targetHint, mapsExisting);

        // ── SECRET — CC6.1 (logical access to sensitive resources) ──
        Add(SecretRead, AuditCategory.Secret, AuditSeverity.Notice, "CC6.1", "secret", true);
        Add(SecretWrite, AuditCategory.Secret, AuditSeverity.Warning, "CC6.1", "secret", true);
        Add(SecretReveal, AuditCategory.Secret, AuditSeverity.Critical, "CC6.1", "secret", true);
        Add(SecretRotateStarted, AuditCategory.Secret, AuditSeverity.Notice, "CC6.1", "secret", true);
        Add(SecretRotateSucceeded, AuditCategory.Secret, AuditSeverity.Notice, "CC6.1", "secret", true);
        Add(SecretRotateFailed, AuditCategory.Secret, AuditSeverity.Warning, "CC6.1", "secret", true);
        Add(SecretVersionRevoked, AuditCategory.Secret, AuditSeverity.Warning, "CC6.1", "secret", true);

        // ── RBAC — CC6.3 (role-based access / least privilege) ──
        Add(TenantMemberRoleChanged, AuditCategory.Rbac, AuditSeverity.Warning, "CC6.3", "user", true);
        Add(TenantMemberInvited, AuditCategory.Rbac, AuditSeverity.Notice, "CC6.3", "user", true);
        Add(TenantMemberJoined, AuditCategory.Rbac, AuditSeverity.Notice, "CC6.3", "user", true);
        Add(TenantMemberRemoved, AuditCategory.Rbac, AuditSeverity.Warning, "CC6.3", "user", true);
        Add(TenantOwnershipTransferred, AuditCategory.Rbac, AuditSeverity.Critical, "CC6.3", "tenant", true);
        Add(UserRoleChanged, AuditCategory.Rbac, AuditSeverity.Warning, "CC6.3", "user", false);

        // ── IMPERSONATION — CC6.1 (privileged-access monitoring) ──
        Add(ImpersonationStarted, AuditCategory.Impersonation, AuditSeverity.Critical, "CC6.1", "tenant", true);
        Add(ImpersonationEnded, AuditCategory.Impersonation, AuditSeverity.Notice, "CC6.1", "tenant", true);

        // ── CONFIG — CC8.1 (change management) ──
        Add(ConventionCreated, AuditCategory.Config, AuditSeverity.Notice, "CC8.1", "convention", true);
        Add(ConventionUpdated, AuditCategory.Config, AuditSeverity.Notice, "CC8.1", "convention", true);
        Add(ConventionDeleted, AuditCategory.Config, AuditSeverity.Warning, "CC8.1", "convention", true);
        Add(ConventionReset, AuditCategory.Config, AuditSeverity.Notice, "CC8.1", "convention", true);
        Add(AgentConfigUpdated, AuditCategory.Config, AuditSeverity.Notice, "CC8.1", "agent_config", true);
        // Story 46-1 (AC8) — provider settings (model selection / enable flag)
        // are configuration change-management, not key custody: Config/CC8.1,
        // deliberately NOT AuditCategory.Byok.
        Add(ProviderSettingsChanged, AuditCategory.Config, AuditSeverity.Notice, "CC8.1", "provider", true);
        Add(SanitizationRuleChanged, AuditCategory.Config, AuditSeverity.Warning, "CC8.1", "sanitization_rule", false);

        // ── PERSONA — CC8.1 (prompt/persona are the agent's behavioural config) ──
        Add(PromptCreated, AuditCategory.Persona, AuditSeverity.Notice, "CC8.1", "prompt", true);
        Add(PromptUpdated, AuditCategory.Persona, AuditSeverity.Notice, "CC8.1", "prompt", true);
        Add(PromptDeleted, AuditCategory.Persona, AuditSeverity.Warning, "CC8.1", "prompt", true);
        Add(PromptReset, AuditCategory.Persona, AuditSeverity.Notice, "CC8.1", "prompt", true);

        // ── BYOK — CC6.1 (provider credential / chain configuration) ──
        Add(ProviderKeyChanged, AuditCategory.Byok, AuditSeverity.Warning, "CC6.1", "provider", true);
        Add(ProviderChainChanged, AuditCategory.Byok, AuditSeverity.Notice, "CC6.1", "provider", false);
        Add(IntegrationCredentialChanged, AuditCategory.Byok, AuditSeverity.Warning, "CC6.1", "integration", true);

        // ── BILLING — A1.1 (commitments affecting availability/spend) ──
        Add(BillingCustomerCreated, AuditCategory.Billing, AuditSeverity.Notice, "A1.1", "tenant", true);
        Add(PlanUpdated, AuditCategory.Billing, AuditSeverity.Warning, "A1.1", "plan", true);
        Add(PlanVersionCreated, AuditCategory.Billing, AuditSeverity.Notice, "A1.1", "plan", true);
        Add(BudgetChanged, AuditCategory.Billing, AuditSeverity.Warning, "A1.1", "budget", false);

        // ── EXPORT — P (privacy / GDPR data-subject access) + C1.1 (confidentiality) ──
        Add(DataExported, AuditCategory.Export, AuditSeverity.Warning, "C1.1", "export", false);
        Add(DsarRequested, AuditCategory.Export, AuditSeverity.Warning, "P6.1", "user", false);

        // ── AUTH — CC6.1 (authentication events) ──
        Add(LogoutAll, AuditCategory.Auth, AuditSeverity.Notice, "CC6.1", "user", true);
        Add(OrgSwitched, AuditCategory.Auth, AuditSeverity.Info, "CC6.1", "user", true);
        Add(RefreshReuseDetected, AuditCategory.Auth, AuditSeverity.Critical, "CC6.1", "user", true);
        // Story 37-10 wired the emitters for these — flip MapsExistingEmitter true.
        Add(LoginSuccess, AuditCategory.Auth, AuditSeverity.Info, "CC6.1", "user", true);
        Add(LoginFailure, AuditCategory.Auth, AuditSeverity.Notice, "CC6.1", "user", true);
        Add(TokenRefreshed, AuditCategory.Auth, AuditSeverity.Info, "CC6.1", "user", true);
        Add(ApiKeyUsed, AuditCategory.Auth, AuditSeverity.Info, "CC6.1", "api_key", true);
        Add(PasswordReset, AuditCategory.Auth, AuditSeverity.Warning, "CC6.1", "user", false);

        // ── TENANT lifecycle — A1.2 (provisioning/de-provisioning) ──
        Add(TenantCreated, AuditCategory.Tenant, AuditSeverity.Notice, "A1.2", "tenant", true);
        Add(TenantProvisioned, AuditCategory.Tenant, AuditSeverity.Notice, "A1.2", "tenant", true);
        Add(TenantDeleted, AuditCategory.Tenant, AuditSeverity.Warning, "A1.2", "tenant", true);
        Add(TenantPurged, AuditCategory.Tenant, AuditSeverity.Critical, "A1.2", "tenant", true);
        Add(TenantMoveRequested, AuditCategory.Tenant, AuditSeverity.Notice, "A1.2", "tenant", true);

        // ── AGENT — CC8.1 (autonomous code actions are change-management evidence) ──
        Add(AgentCreated, AuditCategory.Agent, AuditSeverity.Notice, "CC8.1", "agent", true);
        Add(AgentArchived, AuditCategory.Agent, AuditSeverity.Notice, "CC8.1", "agent", true);
        Add(AgentVersionPublished, AuditCategory.Agent, AuditSeverity.Notice, "CC8.1", "agent", true);
        Add(AgentDispatchSucceeded, AuditCategory.Agent, AuditSeverity.Info, "CC8.1", "agent", true);
        Add(AgentDispatchFailed, AuditCategory.Agent, AuditSeverity.Warning, "CC8.1", "agent", true);
        Add(CodeGeneratedFailed, AuditCategory.Agent, AuditSeverity.Warning, "CC8.1", "issue", true);

        ByCode = map;
    }
}
