using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.PromptStore;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Security;

/// <summary>
/// Story 32-4 — the SaaS provider gate: composition step 1 of the call-LLM
/// endpoint (<c>POST /api/v1/llm/call</c>). See <see cref="ISaaSProviderGate"/>.
///
/// <para>Single-user mode ⇒ hard no-op (Allow, no lookup, no event, no metric).
/// SaaS mode ⇒ classify via <see cref="IProviderAuthLookup"/> (fail-closed on
/// unknown), deny <c>cli-token</c> / unknown (400), check entitlement for
/// <c>api-key</c> (403 if not entitled), else allow. The gate touches provider
/// NAMES and the mode only — it has NO credential / secret dependency.</para>
/// </summary>
public sealed class SaaSProviderGate : ISaaSProviderGate
{
    private readonly ITammaModeProvider _mode;
    private readonly IProviderAuthLookup _authLookup;
    private readonly ITenantProviderEntitlement _entitlement;
    private readonly IEventRepository _events;
    private readonly ProviderGatingMetrics _metrics;
    private readonly ILogger<SaaSProviderGate> _logger;

    public SaaSProviderGate(
        ITammaModeProvider mode,
        IProviderAuthLookup authLookup,
        ITenantProviderEntitlement entitlement,
        IEventRepository events,
        ProviderGatingMetrics metrics,
        ILogger<SaaSProviderGate> logger)
    {
        _mode = mode ?? throw new ArgumentNullException(nameof(mode));
        _authLookup = authLookup ?? throw new ArgumentNullException(nameof(authLookup));
        _entitlement = entitlement ?? throw new ArgumentNullException(nameof(entitlement));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ProviderGateDecision> InspectAsync(
        ProviderGateContext ctx, CancellationToken ct = default)
    {
        // Contract violation — the ONLY case the gate may throw.
        ArgumentNullException.ThrowIfNull(ctx);

        // 1. single-user / self-hosted: hard no-op. Harness providers are a
        //    legitimate local affordance — no lookup, no event, no metric.
        if (_mode.Mode != TammaMode.SaaS)
        {
            _logger.LogDebug(
                "SaaS provider gate no-op (single-user mode): provider={Provider}",
                ctx.ProviderName);
            return ProviderGateDecision.Allow(model: null);
        }

        // 2. SaaS: classify the provider (fail-closed on unknown).
        //    AC4: eligibility that CANNOT be determined (the entity read throws —
        //    a transient Npgsql/DbException, the future Epic-34 lookup, etc.) ⇒
        //    DENY, never a leaked 500 and never a silent allow. Cancellation is
        //    NOT a denial — it must propagate.
        ProviderAuthModel? authModel;
        try
        {
            authModel = await _authLookup.AuthModelAsync(ctx.ProviderName, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancellation is not a gate denial — let it propagate.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SaaS provider gate could not determine eligibility (lookup failed); "
                + "failing closed (deny). provider={Provider}, role={Role}, action={Action}, "
                + "tenantId={TenantId}",
                ctx.ProviderName, ctx.Role, ctx.Action, ctx.TenantId);

            await EmitGatedAsync(ctx, authModel: null, "ELIGIBILITY_UNAVAILABLE", ct);
            return new ProviderGateDecision(
                false,
                ProviderGateOutcome.SaasProviderNotAllowed,
                Reason: "Provider eligibility could not be determined; denied.",
                AuthModel: null,
                HttpStatusHint: 400);
        }

        if (authModel is null || authModel == ProviderAuthModel.CliToken)
        {
            var reason = authModel is null ? "PROVIDER_UNKNOWN" : "CLI_TOKEN_PROVIDER";

            if (authModel is null)
            {
                _logger.LogWarning(
                    "SaaS provider gate denied unknown provider (fail-closed): "
                    + "provider={Provider}, role={Role}, action={Action}, tenantId={TenantId}",
                    ctx.ProviderName, ctx.Role, ctx.Action, ctx.TenantId);
            }
            else
            {
                _logger.LogInformation(
                    "SaaS provider gated: provider={Provider}, authModel=cli-token, "
                    + "outcome=SaasProviderNotAllowed, reason={Reason}, role={Role}, "
                    + "action={Action}, tenantId={TenantId}",
                    ctx.ProviderName, reason, ctx.Role, ctx.Action, ctx.TenantId);
            }

            await EmitGatedAsync(ctx, authModel, reason, ct);
            return new ProviderGateDecision(
                false,
                ProviderGateOutcome.SaasProviderNotAllowed,
                Reason: $"Provider '{ctx.ProviderName}' is not available in SaaS mode "
                    + "(api-key providers only).",
                AuthModel: authModel,
                HttpStatusHint: 400);
        }

        // 3. SaaS auth / entitlement (Epic 34 seam). Provider is api-key — is the
        //    tenant entitled to the managed-LLM path for it?
        //    AC4: an entitlement check that THROWS means we cannot determine the
        //    tenant is entitled ⇒ fail-closed DENY (never a silent allow, never a
        //    leaked 500). We map this to SaasProviderNotAllowed/400 (reason
        //    ENTITLEMENT_UNAVAILABLE) rather than TenantNotEntitled/403: 403 is a
        //    DETERMINED "not entitled" verdict, whereas a thrown exception is an
        //    UNDETERMINED eligibility — the same §2.4 "could not determine ⇒ 400"
        //    category as a lookup failure above. Cancellation still propagates.
        bool entitled;
        try
        {
            entitled = await _entitlement.IsTenantEntitledAsync(ctx.TenantId, ctx.ProviderName, ct);
        }
        catch (OperationCanceledException)
        {
            throw; // a cancellation is not a gate denial — let it propagate.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SaaS provider gate could not determine entitlement (check failed); "
                + "failing closed (deny). provider={Provider}, role={Role}, action={Action}, "
                + "tenantId={TenantId}",
                ctx.ProviderName, ctx.Role, ctx.Action, ctx.TenantId);

            await EmitGatedAsync(ctx, authModel, "ENTITLEMENT_UNAVAILABLE", ct);
            return new ProviderGateDecision(
                false,
                ProviderGateOutcome.SaasProviderNotAllowed,
                Reason: "Tenant entitlement could not be determined; denied.",
                AuthModel: authModel,
                HttpStatusHint: 400);
        }

        if (!entitled)
        {
            _logger.LogInformation(
                "SaaS provider gated: provider={Provider}, authModel=api-key, "
                + "outcome=TenantNotEntitled, reason=TENANT_NOT_ENTITLED, role={Role}, "
                + "action={Action}, tenantId={TenantId}",
                ctx.ProviderName, ctx.Role, ctx.Action, ctx.TenantId);

            await EmitGatedAsync(ctx, authModel, "TENANT_NOT_ENTITLED", ct);
            return new ProviderGateDecision(
                false,
                ProviderGateOutcome.TenantNotEntitled,
                Reason: "Tenant is not entitled to the managed LLM path for this provider.",
                AuthModel: authModel,
                HttpStatusHint: 403);
        }

        // api-key + entitled ⇒ allow (no event, no metric).
        _logger.LogDebug(
            "SaaS provider gate allowed: provider={Provider}, authModel=api-key",
            ctx.ProviderName);
        return ProviderGateDecision.Allow(authModel);
    }

    /// <summary>
    /// Emit exactly one <c>AGENT.PROVIDER.GATED</c> DCB event via the tenant
    /// <see cref="IEventRepository"/> and increment the
    /// <c>tamma.provider.gated</c> counter once. The event append is best-effort:
    /// a failure is logged at ERROR and SWALLOWED so a clean typed decision never
    /// becomes a 500. The metric is incremented regardless of append success.
    /// </summary>
    private async Task EmitGatedAsync(
        ProviderGateContext ctx, ProviderAuthModel? authModel, string reason, CancellationToken ct)
    {
        var authModelTag = AuthModelTag(authModel);

        // Metric first — a denial is always counted even if the event store is down.
        _metrics.RecordGated(ctx.ProviderName, authModelTag, reason);

        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = "AGENT.PROVIDER.GATED",
                TenantId = ctx.TenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = ctx.TenantId?.ToString(),
                    provider = ctx.ProviderName,
                    authModel = authModelTag,
                    mode = "saas",
                    role = ctx.Role,
                    action = ctx.Action,
                }),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = JsonSerializer.Serialize(new
                {
                    provider = ctx.ProviderName,
                    authModel = authModelTag,
                    mode = "saas",
                    reason,
                    role = ctx.Role,
                    action = ctx.Action,
                }),
                CreatedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            // Swallow — the deny/allow decision is never masked by an event-store
            // failure. The metric was already incremented above.
            _logger.LogError(ex,
                "AGENT.PROVIDER.GATED event append failed; the typed gate decision "
                + "still returns. provider={Provider}, reason={Reason}, tenantId={TenantId}",
                ctx.ProviderName, reason, ctx.TenantId);
        }
    }

    private static string AuthModelTag(ProviderAuthModel? authModel) => authModel switch
    {
        ProviderAuthModel.CliToken => "cli-token",
        ProviderAuthModel.ApiKey => "api-key",
        _ => "unknown",
    };
}
