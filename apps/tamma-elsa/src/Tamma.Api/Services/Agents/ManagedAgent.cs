using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Providers;
using Tamma.Api.Services.Security;
using Tamma.Core;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 (T3, AC3/AC8/AC10) — the managed execution layer behind
/// <c>POST /api/v1/llm/call</c>. Composes the rule-2 sequence ENTIRELY inside
/// <c>Tamma.Api</c> and ALWAYS returns a typed <see cref="AgentRunResult"/>: a
/// failure NEVER loses the run record and ALWAYS emits exactly one terminal
/// <c>AGENT.RUN.*</c> event.
///
/// <para><b>Compose order (with one operational deviation from the doc's
/// numbering, documented for T4):</b></para>
/// <list type="number">
///   <item><description><b>resolve agent + enablement + prompt</b> (32-2/32-18,
///     applying 32-16; the resolver ALSO renders the prompt — Epic 27 persona /
///     32-17 custom — so the doc's "step 4 render" is INTERNAL to this step).
///     Done FIRST because the gate (and credential) key off the resolved
///     <c>provider</c>, which is unknown until resolution.</description></item>
///   <item><description><b>gate</b> (32-4 <see cref="ISaaSProviderGate"/>) — on
///     the now-known provider name. A denial short-circuits to a failed run
///     (400 <c>SAAS_PROVIDER_NOT_ALLOWED</c> / 403 <c>TENANT_NOT_ENTITLED</c>)
///     BEFORE any credential resolution or provider call (fail-closed).</description></item>
///   <item><description><b>budget</b> (1b — <see cref="IBudgetGuard"/>,
///     fail-closed; over-budget ⇒ loop never invoked).</description></item>
///   <item><description><b>credential</b> (32-3 cabinet, BYOK→platform;
///     unavailable ⇒ <c>PROVIDER_CREDENTIAL_UNAVAILABLE</c>, provider never
///     called).</description></item>
///   <item><description>emit <c>AGENT.RUN.STARTED</c> (before the loop).</description></item>
///   <item><description><b>runner</b> (<see cref="IInlineToolLoopRunner"/>,
///     request-scoped key; provider error ⇒ <c>PROVIDER_ERROR</c> + preserved
///     <c>httpStatusCode</c>; exhausted, no usable text ⇒
///     <c>LOOP_EXHAUSTED</c>).</description></item>
///   <item><description><b>meter</b> (34-11 cost basis + 34-5 markup + 32-9
///     usage) → <b>terminal</b> <c>AGENT.RUN.SUCCESS</c>/<c>FAILED</c>.</description></item>
/// </list>
/// </summary>
public sealed class ManagedAgent : IManagedAgent
{
    private readonly ISaaSProviderGate _gate;
    private readonly IBudgetGuard _budget;
    private readonly IAgentResolverService _resolver;
    private readonly IProviderCredentialResolver _credentials;
    private readonly IInlineToolLoopRunner _runner;
    private readonly IProviderPricingService _pricing;
    private readonly IProviderMarkupEngine _markup;       // 34-5 (interim seam)
    private readonly IUsageEmitter _usage;                // 32-9 (interim seam)
    private readonly IEventRepository _events;
    // Finding I-3 — the SAME input sanitizer the legacy CallLlmInlineActivity ran
    // (Tamma.Activities.Security.IContentSanitizer.SanitizeInput): injection
    // detection + HTML strip + null/zero-width removal on the rendered system
    // prompt + the user prompt BEFORE the provider call. Optional (null ⇒ skip,
    // matching the legacy `if (_sanitizer == null) return raw;`).
    private readonly Tamma.Activities.Security.IContentSanitizer? _sanitizer;
    private readonly ILogger<ManagedAgent> _logger;

    // NOTE: the process mode (single-user vs SaaS) is NOT a dependency here — the
    // ONLY mode decision in this composition lives inside ISaaSProviderGate (32-4),
    // which reads ITammaModeProvider itself. ManagedAgent never branches on mode.

    public ManagedAgent(
        ISaaSProviderGate gate,
        IBudgetGuard budget,
        IAgentResolverService resolver,
        IProviderCredentialResolver credentials,
        IInlineToolLoopRunner runner,
        IProviderPricingService pricing,
        IProviderMarkupEngine markup,
        IUsageEmitter usage,
        IEventRepository events,
        ILogger<ManagedAgent> logger,
        Tamma.Activities.Security.IContentSanitizer? sanitizer = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _pricing = pricing ?? throw new ArgumentNullException(nameof(pricing));
        _markup = markup ?? throw new ArgumentNullException(nameof(markup));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Optional (Finding I-3): when absent, prompts pass through unsanitized —
        // identical to the legacy `if (_sanitizer == null) return raw;`. It is
        // DI-registered in the API host (Program.cs), so production always has it.
        _sanitizer = sanitizer;
    }

    /// <inheritdoc />
    public async Task<AgentRunResult> RunAsync(ManagedAgentRequest request, CancellationToken ct = default)
    {
        // The ONLY case that may throw (a contract violation, AC10).
        ArgumentNullException.ThrowIfNull(request);

        var sw = Stopwatch.StartNew();

        // ── carries the identity stamped as composition advances, so a failure
        //    at ANY step still produces a fully-tagged terminal event + record.
        var ctx = new RunContext
        {
            TenantId = request.TenantId,
            Role = request.Role,
            CorrelationId = request.CorrelationId,
        };

        try
        {
            // ── 1. resolve agent + enablement + prompt (32-2/32-18; renders prompt) ──
            ResolvedAgentConfig resolved;
            try
            {
                resolved = string.IsNullOrWhiteSpace(request.Phase)
                    ? await _resolver.ResolveForRoleAsync(request.Role, request.Action, ct)
                        .ConfigureAwait(false)
                    : await _resolver.ResolveForRoleAndPhaseAsync(request.Phase, request.Role, ct)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TammaError ex)
            {
                // No enabled default / unresolved prompt / custom-prompt-unresolved —
                // a CONFIG fail-closed (no provider call), NOT a credential problem.
                // ⇒ AGENT_UNRESOLVED (non-retryable, 422 inside a 200 envelope).
                return await FailAsync(ctx, sw, AgentRunFailureCodes.AgentUnresolved,
                    ex.Message, httpStatus: 422, ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                // An unknown role / bad argument from the resolver is a config /
                // validation error — NOT a provider failure. ⇒ AGENT_UNRESOLVED.
                return await FailAsync(ctx, sw, AgentRunFailureCodes.AgentUnresolved,
                    $"agent resolution rejected the request: {ex.Message}",
                    httpStatus: 422, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.ProviderError,
                    $"agent resolution failed: {ex.Message}", httpStatus: null, ct).ConfigureAwait(false);
            }

            // Finding I-1 — honour the workflow's per-iteration provider override.
            // The role resolves the prompt/tools/budget/model-preference; but when
            // the request names an explicit provider (the ForEach<provider> chain),
            // THAT provider is the one this call runs against — so the gate, the
            // credential resolution, and the runner all key off it, making the
            // chain meaningful again. When no override is given, the role-resolved
            // provider stands. The model follows the provider: an explicit
            // request.Model wins; else the resolved model if it belongs to the
            // chosen provider; else the chosen provider's own default model
            // (never a model from a different provider).
            ctx.Provider = ResolveProvider(request, resolved);
            ctx.Model = ResolveModel(request, resolved, ctx.Provider);
            ctx.AgentId = resolved.AgentId;
            ctx.Version = resolved.AgentVersion ?? 0;

            // ── 2. gate (32-4) — on the resolved provider name (fail-closed) ──
            ProviderGateDecision gate;
            try
            {
                gate = await _gate.InspectAsync(
                    new ProviderGateContext(ctx.Provider, request.Role, request.Action, request.TenantId),
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.SaasProviderNotAllowed,
                    $"gate evaluation failed: {ex.Message}", httpStatus: 400, ct).ConfigureAwait(false);
            }

            if (!gate.Allowed)
            {
                var code = gate.Outcome == ProviderGateOutcome.TenantNotEntitled
                    ? AgentRunFailureCodes.TenantNotEntitled
                    : AgentRunFailureCodes.SaasProviderNotAllowed;
                return await FailAsync(ctx, sw, code, gate.Reason ?? "provider gated",
                    httpStatus: gate.HttpStatusHint, ct).ConfigureAwait(false);
            }

            // ── 1b. budget (fail-closed) ──
            bool withinBudget;
            try
            {
                withinBudget = await _budget
                    .IsWithinBudgetAsync(request.TenantId, request.Params.BudgetCapUsd, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.BudgetExceeded,
                    $"budget evaluation failed: {ex.Message}", httpStatus: null, ct).ConfigureAwait(false);
            }

            if (!withinBudget)
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.BudgetExceeded,
                    "per-call budget cap exceeded", httpStatus: null, ct).ConfigureAwait(false);
            }

            // ── 3. credential (32-3 cabinet, BYOK→platform; fail-closed) ──
            ProviderCredential credential;
            try
            {
                credential = await _credentials.ResolveAsync(request.TenantId, ctx.Provider, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (TammaError ex) when (ex.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE")
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.CredentialUnavailable,
                    ex.Message, httpStatus: null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await FailAsync(ctx, sw, AgentRunFailureCodes.CredentialUnavailable,
                    $"credential resolution failed: {ex.Message}", httpStatus: null, ct).ConfigureAwait(false);
            }

            // Credential safety: the key lives only on this request-scoped config,
            // used for the outbound header inside the runner, never logged/returned.
            ctx.CredentialSource = CredentialSourceLabel.From(credential.Source);

            // ── 4. (render) — already done by the resolver: resolved.SystemPrompt ──

            // ── 5. AGENT.RUN.STARTED (exactly one, before the loop) ──
            await EmitAsync(AgentRunEventTypes.Started, ctx, failureCode: null, ct)
                .ConfigureAwait(false);

            // ── 5b. (Finding I-3) sanitize the input prompts SERVER-SIDE before the
            //     provider call — the same defence-in-depth the legacy activity ran
            //     (SanitizePrompts → injection detection + HTML strip + null/
            //     zero-width removal). The runner sanitizes tool OUTPUTS; the INPUT
            //     system+user prompts are sanitized HERE so a poisoned rendered
            //     prompt never reaches the provider. ──
            var systemPrompt = SanitizeInputPrompt(resolved.SystemPrompt, "SystemPrompt", ctx);
            var userPrompt = SanitizeInputPrompt(request.Prompt, "UserPrompt", ctx);

            // ── 6. provider call via the extracted runner (request-scoped key) ──
            var providerConfig = new LlmProviderConfig
            {
                Name = ctx.Provider,
                ApiKey = credential.ApiKey, // request-scoped; dropped after the call
            };

            InlineToolLoopResult loop;
            try
            {
                loop = await _runner.RunAsync(
                    provider: ctx.Provider,
                    providerConfig: providerConfig,
                    model: ctx.Model,
                    systemPrompt: systemPrompt,
                    userPrompt: userPrompt,
                    maxTokens: request.Params.MaxTokens,
                    temperature: request.Params.Temperature,
                    tools: ToResolvedTools(request, resolved),
                    enableToolLoop: request.EnableToolLoop,
                    loopConfig: request.ToolLoopConfig ?? new ToolLoopConfig(),
                    correlationId: request.CorrelationId,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // An unexpected runner throw is still a typed run record (not lost).
                // CREDENTIAL SAFETY (load-bearing): the request-scoped key is on the
                // providerConfig handed to the runner, so a misbehaving runner COULD
                // echo it into ex.Message. We therefore NEVER interpolate the raw
                // collaborator exception message into the caller-facing FailureReason
                // (which is returned to the caller AND logged); the full detail is
                // captured server-side via the structured ERROR log below.
                _logger.LogError(ex,
                    "Inline tool-loop runner threw; failing the run as PROVIDER_ERROR. "
                    + "provider={Provider}, role={Role}, correlationId={CorrelationId}, tenantId={TenantId}",
                    ctx.Provider, ctx.Role, ctx.CorrelationId, ctx.TenantId);
                return await FailTerminalAsync(ctx, sw, AgentRunFailureCodes.ProviderError,
                    "provider call failed", httpStatus: 0,
                    inTok: 0, outTok: 0, toolLoopTokens: 0, turns: 0, exhausted: false, ct)
                    .ConfigureAwait(false);
            }

            var inTok = loop.InputTokens;
            var outTok = loop.OutputTokens;
            var toolLoopTokens = request.EnableToolLoop ? inTok + outTok : 0;

            // ── provider error (preserve httpStatusCode) ──
            if (!loop.Response.Success)
            {
                return await FailTerminalAsync(ctx, sw, AgentRunFailureCodes.ProviderError,
                    loop.Response.ErrorMessage ?? "provider call failed",
                    httpStatus: loop.Response.HttpStatusCode,
                    inTok, outTok, toolLoopTokens, loop.Turns, loop.Exhausted, ct)
                    .ConfigureAwait(false);
            }

            // ── loop exhausted with no usable response ──
            if (loop.Exhausted && string.IsNullOrEmpty(loop.Response.ResponseText))
            {
                return await FailTerminalAsync(ctx, sw, AgentRunFailureCodes.LoopExhausted,
                    "tool loop exhausted maxSteps with no usable response",
                    httpStatus: loop.Response.HttpStatusCode,
                    inTok, outTok, toolLoopTokens, loop.Turns, loop.Exhausted, ct)
                    .ConfigureAwait(false);
            }

            // ── 7. meter (cost basis + markup + usage) ──
            var costBasis = _pricing.Compute(ctx.Provider, ctx.Model, inTok, outTok);
            var price = _markup.Apply(costBasis, ctx.CredentialSource, ctx.Provider, ctx.Model, request.TenantId);

            var result = new AgentRunResult
            {
                AgentId = ctx.AgentId,
                Version = ctx.Version,
                Provider = ctx.Provider,
                Model = ctx.Model,
                Role = request.Role,
                InputTokens = inTok,
                OutputTokens = outTok,
                CostUsd = costBasis,
                PriceUsd = price,
                ToolLoopTokens = toolLoopTokens,
                ToolLoopTurns = loop.Turns,
                ToolLoopExhausted = loop.Exhausted,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true,
                ToolCalls = loop.ToolCalls.Select(tc => new ToolCallDto
                {
                    Name = tc.ToolName,
                    Id = tc.ToolCallId,
                    ArgumentsJson = "{}",
                }).ToList(),
                CorrelationId = request.CorrelationId,
                CredentialSource = ctx.CredentialSource,
                ResponseText = loop.Response.ResponseText,
            };

            await EmitUsageAsync(result, request.TenantId, ct).ConfigureAwait(false);

            // ── 8. terminal AGENT.RUN.SUCCESS ──
            await EmitAsync(AgentRunEventTypes.Success, ctx, failureCode: null, ct)
                .ConfigureAwait(false);

            return result;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not a lost run; let it propagate (the host aborts).
            throw;
        }
    }

    // -----------------------------------------------------------------------
    // failure helpers — every failure produces a record + one terminal FAILED
    // -----------------------------------------------------------------------

    /// <summary>Fail BEFORE the STARTED event was emitted (gate / budget /
    /// credential / resolve). Emits exactly one terminal FAILED.</summary>
    private async Task<AgentRunResult> FailAsync(
        RunContext ctx, Stopwatch sw, string failureCode, string reason, int? httpStatus,
        CancellationToken ct)
        => await FailTerminalAsync(ctx, sw, failureCode, reason, httpStatus,
            inTok: 0, outTok: 0, toolLoopTokens: 0, turns: 0, exhausted: false, ct)
            .ConfigureAwait(false);

    /// <summary>Build the failed record, emit exactly one terminal FAILED, and
    /// return. Used for every failure path (pre- and post-loop) so the
    /// "exactly one terminal AGENT.RUN.* per run" invariant always holds.
    /// <para>No <c>IUsageEmitter</c> record is emitted on failure paths — that is
    /// a deliberate 32-9-era decision: the durable signal for a metered-but-failed
    /// run is the terminal <c>AGENT.RUN.FAILED</c> DCB event, not a usage row.</para></summary>
    private async Task<AgentRunResult> FailTerminalAsync(
        RunContext ctx, Stopwatch sw, string failureCode, string reason, int? httpStatus,
        int inTok, int outTok, int toolLoopTokens, int turns, bool exhausted, CancellationToken ct)
    {
        _logger.LogWarning(
            "Managed run failed: failureCode={FailureCode}, httpStatus={HttpStatus}, "
            + "provider={Provider}, role={Role}, correlationId={CorrelationId}, tenantId={TenantId}",
            failureCode, httpStatus, ctx.Provider, ctx.Role, ctx.CorrelationId, ctx.TenantId);

        var result = new AgentRunResult
        {
            AgentId = ctx.AgentId,
            Version = ctx.Version,
            Provider = ctx.Provider ?? string.Empty,
            Model = ctx.Model ?? string.Empty,
            Role = ctx.Role,
            InputTokens = inTok,
            OutputTokens = outTok,
            CostUsd = 0m,
            PriceUsd = 0m,
            ToolLoopTokens = toolLoopTokens,
            ToolLoopTurns = turns,
            ToolLoopExhausted = exhausted,
            DurationMs = sw.ElapsedMilliseconds,
            Success = false,
            CorrelationId = ctx.CorrelationId,
            CredentialSource = ctx.CredentialSource,
            FailureCode = failureCode,
            FailureReason = reason,
            HttpStatusCode = httpStatus,
        };

        await EmitAsync(AgentRunEventTypes.Failed, ctx, failureCode, ct).ConfigureAwait(false);
        return result;
    }

    // -----------------------------------------------------------------------
    // events / usage (best-effort — never converts a returned run into a loss)
    // -----------------------------------------------------------------------

    private async Task EmitAsync(string type, RunContext ctx, string? failureCode, CancellationToken ct)
    {
        try
        {
            object tagsObj = failureCode is null
                ? new
                {
                    agentId = ctx.AgentId?.ToString(),
                    version = ctx.Version,
                    provider = ctx.Provider,
                    model = ctx.Model,
                    role = ctx.Role,
                    correlationId = ctx.CorrelationId,
                    credentialSource = ctx.CredentialSource,
                    tenantId = ctx.TenantId?.ToString(),
                }
                : new
                {
                    agentId = ctx.AgentId?.ToString(),
                    version = ctx.Version,
                    provider = ctx.Provider,
                    model = ctx.Model,
                    role = ctx.Role,
                    correlationId = ctx.CorrelationId,
                    credentialSource = ctx.CredentialSource,
                    tenantId = ctx.TenantId?.ToString(),
                    failureCode,
                };

            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = type,
                TenantId = ctx.TenantId,
                Tags = JsonSerializer.Serialize(tagsObj),
                Metadata = JsonSerializer.Serialize(new
                {
                    workflowVersion = "1.0.0",
                    eventSource = "system",
                }),
                Data = "{}",
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // AC8 / logging-requirements: an append failure is logged at ERROR,
            // NOT swallowed silently into losing the run — the run still returns.
            _logger.LogError(ex,
                "AGENT.RUN.* event append failed (type={Type}); the run result still returns. "
                + "correlationId={CorrelationId}, tenantId={TenantId}",
                type, ctx.CorrelationId, ctx.TenantId);
        }
    }

    private async Task EmitUsageAsync(AgentRunResult run, Guid? tenantId, CancellationToken ct)
    {
        try
        {
            await _usage.EmitAsync(new UsageRecord
            {
                TenantId = tenantId,
                AgentId = run.AgentId,
                Provider = run.Provider,
                Model = run.Model,
                Role = run.Role,
                InputTokens = run.InputTokens,
                OutputTokens = run.OutputTokens,
                ProviderCostUsd = run.CostUsd,
                PriceUsd = run.PriceUsd,
                CredentialSource = run.CredentialSource,
                CorrelationId = run.CorrelationId,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Usage emission failed; the run result still returns. correlationId={CorrelationId}",
                run.CorrelationId);
        }
    }

    // -----------------------------------------------------------------------
    // pure helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Finding I-3 — sanitize one INPUT prompt before the provider call (the same
    /// <c>SanitizeInput</c> the legacy <c>CallLlmInlineActivity.SanitizePrompts</c>
    /// ran: injection detection + HTML strip + null/zero-width removal). When the
    /// sanitizer is absent (null) the raw text passes through, matching the
    /// legacy `if (_sanitizer == null) return raw;`. Detected injection patterns
    /// are logged at WARN with a COUNT only (never the prompt body — it may carry
    /// secrets), mirroring the legacy log.
    /// </summary>
    private string SanitizeInputPrompt(string? raw, string field, RunContext ctx)
    {
        if (_sanitizer is null || string.IsNullOrEmpty(raw))
        {
            return raw ?? string.Empty;
        }

        var result = _sanitizer.SanitizeInput(raw);
        if (result.Warnings.Count > 0)
        {
            _logger.LogWarning(
                "Injection pattern detected in {Field} for managed run; patterns matched: {Count}, "
                + "provider={Provider}, role={Role}, correlationId={CorrelationId}, tenantId={TenantId}",
                field, result.Warnings.Count, ctx.Provider, ctx.Role, ctx.CorrelationId, ctx.TenantId);
        }

        return result.Result;
    }

    /// <summary>Finding I-1 — the provider this call runs against: the explicit
    /// <see cref="ManagedAgentRequest.Provider"/> override (the workflow's
    /// per-iteration provider) when set, else the role-resolved provider.</summary>
    private static string ResolveProvider(ManagedAgentRequest request, ResolvedAgentConfig resolved)
        => !string.IsNullOrWhiteSpace(request.Provider) ? request.Provider!.Trim() : resolved.Provider;

    /// <summary>
    /// Finding I-1 — pick the model for <paramref name="chosenProvider"/>:
    /// an explicit <see cref="ManagedAgentRequest.Model"/> wins; else the resolved
    /// model IF the chosen provider is the role-resolved provider (i.e. no
    /// override, or the override matches); else the chosen provider's OWN default
    /// model (reusing the runner's legacy <c>LoadProviderConfig</c>/
    /// <c>GetDefaultModel</c>) — never a model that belongs to a different
    /// provider. When the runner has no default for the provider it falls back to
    /// the resolved model rather than an empty string.
    /// </summary>
    private string ResolveModel(ManagedAgentRequest request, ResolvedAgentConfig resolved, string chosenProvider)
    {
        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            return request.Model!;
        }

        var providerMatchesResolved = string.Equals(
            chosenProvider, resolved.Provider, StringComparison.OrdinalIgnoreCase);
        if (providerMatchesResolved)
        {
            return resolved.Model;
        }

        // Override provider differs from the role-resolved one — the resolved model
        // is a model for a DIFFERENT provider, so use the override provider's own
        // default (legacy GetDefaultModel). Guard against an empty default.
        var providerDefault = _runner.GetDefaultModel(chosenProvider);
        return string.IsNullOrWhiteSpace(providerDefault) ? resolved.Model : providerDefault;
    }

    private static IReadOnlyList<ResolvedTool>? ToResolvedTools(
        ManagedAgentRequest request, ResolvedAgentConfig resolved)
    {
        // The request's tool allow-list (else the agent's resolved default set)
        // becomes the resolved-tool set passed to the runner. Per the buffered
        // scope, descriptions/schemas come from the existing built-in catalog at
        // runtime; here we pass the names so the runner advertises them.
        var names = request.Tools is { Count: > 0 } ? request.Tools : resolved.Tools;
        if (names is not { Count: > 0 })
        {
            return null;
        }

        return names.Select(n => new ResolvedTool { Name = n }).ToList();
    }

    /// <summary>Mutable carrier for the identity stamped as composition advances
    /// — so a failure at any step still produces a fully-tagged terminal event.</summary>
    private sealed class RunContext
    {
        public Guid? TenantId { get; init; }
        public required string Role { get; init; }
        public required string CorrelationId { get; init; }
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public Guid? AgentId { get; set; }
        public int Version { get; set; }
        public string? CredentialSource { get; set; }
    }
}
