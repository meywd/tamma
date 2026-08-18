using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Tamma.Activities.Context;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using Tamma.Activities.Security;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// LLM Call Sub-Workflow — the universal building block for all AI operations in Tamma.
///
/// Inputs (via DispatchWorkflow.Input dictionary):
///   - agentRole (string): Agent role for provider chain resolution (e.g. "analyst", "implementer")
///   - taskPrompt (string): User prompt content
///   - context (string): Serialized context object
///   - sessionId (string): Session ID for tracking
///   - InputJson (string): Legacy single-JSON input (fallback)
///
/// Outputs (via SetOutput, readable from DispatchWorkflow.Result):
///   - llmResponse (string): LLM response text
///   - providerUsed (string): Provider that succeeded
///   - costUsd (decimal): Total cost
///   - tokensUsed (int): Total tokens
///   - success (bool): Whether the call succeeded
///   - workflowOutput (string): Full serialized LlmCallWorkflowOutput JSON
///
/// Design: Flowchart with visible nodes for each phase in ELSA Studio.
///
/// Flow:
///   InitInputs → ResolvePrompt → SetupBudget → ResolveAgentConfig → ResolveChain
///     → CheckConcurrency → [OK?]
///       OK      → ForEachProviderChain → FailureCheck → [success?]
///                   Yes → SetOutputs
///                   No  → BuildFailureOutput → SetOutputs
///       AtLimit → ConcurrencyDelay → CheckConcurrency (loop)
/// </summary>
public class LlmCallWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "LLM Call Sub-Workflow";
        builder.DefinitionId = "llm-call";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Universal LLM call with provider chain, circuit breaker, retry, and 6-level prompt resolution";

        // ============================================================
        // Variables
        // ============================================================

        // Input variables (populated from workflow input)
        var agentRoleVar = builder.WithVariable<string>("AgentRole", "assistant").Persisted();
        var actionVar = builder.WithVariable<string>("Action", "").Persisted();
        var variablesJsonVar = builder.WithVariable<string>("VariablesJson", "{}").Persisted();
        var taskPromptVar = builder.WithVariable<string>("TaskPrompt", "").Persisted();
        var contextVar = builder.WithVariable<string>("Context", "").Persisted();
        var sessionIdVar = builder.WithVariable<string>("SessionId", "").Persisted();
        var tenantIdVar = builder.WithVariable<string>("TenantId", "").Persisted();

        // Story 39-9 (D10) — additive/optional repair-ring inputs. Default empty ⇒
        // zero behaviour change for the 30+ existing dispatchers. documentType is the
        // wire KEY gating the server-side repair ring; issueId rides through for the
        // LLM.* event tags; contentValidation surfaces the wire block back to callers.
        var documentTypeVar = builder.WithVariable<string>("DocumentType", "").Persisted();
        var issueIdVar = builder.WithVariable<string>("IssueId", "").Persisted();
        var contentValidationVar = builder.WithVariable<string>("ContentValidationJson", "").Persisted();

        // Legacy input support
        var inputVar = builder.WithVariable<string>("InputJson", "").Persisted();

        // State variables
        var circuitBreakerStatesVar = builder.WithVariable<string>("CircuitBreakerStatesJson", "{}").Persisted();
        var budgetStateVar = builder.WithVariable<string>("BudgetStateJson", "{}").Persisted();
        var diagnosticsListVar = builder.WithVariable<string>("DiagnosticsListJson", "[]").Persisted();
        var workflowOutputVar = builder.WithVariable<string>("WorkflowOutputJson", "").Persisted();
        var successVar = builder.WithVariable<bool>("CallSucceeded", false).Persisted();
        var currentProviderVar = builder.WithVariable<string>("CurrentProvider", "").Persisted();
        var lastDiagnosticVar = builder.WithVariable<string>("LastDiagnostic", "").Persisted();
        var lastResponseVar = builder.WithVariable<string>("LastResponse", "").Persisted();
        var attemptNumberVar = builder.WithVariable<int>("AttemptNumber", 1).Persisted();
        var maxRetriesVar = builder.WithVariable<int>("MaxRetries", 3).Persisted();
        var providerChainVar = builder.WithVariable<object>("ProviderChain", new List<string>()).Persisted();
        var systemPromptOverrideVar = builder.WithVariable<string>("SystemPromptOverride", "").Persisted();
        var resolvedSystemPromptVar = builder.WithVariable<string>("ResolvedSystemPrompt", "").Persisted();
        var resolvedToolsJsonVar = builder.WithVariable<string>("ResolvedToolsJson", "").Persisted();

        // Registry-resolved MaxTokens (ResolvePromptFromRegistryActivity output).
        // 0 = not yet resolved; the activity always writes ≥ 4096 once it runs.
        // Applied to the wire Params.MaxTokens only on the registry path (non-
        // empty action) — see CallLlmInlineActivity.BuildLlmCallRequest.
        var registryMaxTokensVar = builder.WithVariable<int>("RegistryMaxTokens", 0).Persisted();

        // Story 27-13 — conventions resolved from the convention store (or the
        // legacy `.tamma/config.json` string for the empty-action passthrough
        // path). Feeds {{conventions}} in the prompt-render variables.
        var legacyConventionsVar = builder.WithVariable<string>("LegacyConventions", "").Persisted();
        var resolvedConventionsVar = builder.WithVariable<string>("ResolvedConventions", "").Persisted();

        // Tool loop variables
        var enableToolLoopVar = builder.WithVariable<bool>("EnableToolLoop", false).Persisted();
        var toolLoopConfigJsonVar = builder.WithVariable<string>("ToolLoopConfigJson", "").Persisted();
        var toolLoopTokensVar = builder.WithVariable<int>("ToolLoopTokens", 0).Persisted();
        var toolLoopTurnsVar = builder.WithVariable<int>("ToolLoopTurns", 0).Persisted();
        var toolLoopExhaustedVar = builder.WithVariable<bool>("ToolLoopExhausted", false).Persisted();

        // ============================================================
        // Activities
        // ============================================================

        // 1. Initialize from input — supports both new typed inputs and legacy InputJson
        var initInputs = new SetVariable
        {
            Id = "InitInputs",
            Name = "Initialize Inputs",
            Variable = agentRoleVar,
            Value = new(context => {
                // Try new typed inputs first
                // New: role + action + variables pattern (from prompt registry)
                var action = context.GetInput<string>("action") ?? "";
                actionVar.Set(context, action);

                // Capture the legacy `.tamma/config.json` conventions string so
                // ResolveConventionsActivity can use it for the empty-action
                // passthrough path (Story 27-13). When Action is non-empty the
                // store result takes precedence and this is ignored.
                var legacyConv = context.GetInput<string>("conventions") ?? "";
                legacyConventionsVar.Set(context, legacyConv);

                // Serialize variables dict if provided, injecting defaults for common template vars
                var variables = context.GetInput<IDictionary<string, object>>("variables");
                if (variables != null)
                {
                    // Inject 'role' if not provided — every template uses {{role}}
                    if (!variables.ContainsKey("role"))
                    {
                        var r = context.GetInput<string>("agentRole") ?? context.GetInput<string>("role") ?? "assistant";
                        variables["role"] = r;
                    }
                    // 'conventions' will be overwritten downstream by the
                    // ResolveConventionsActivity result; leave a placeholder
                    // here so callers that bypass the resolve activity (or
                    // use the empty-action path) still see a sensible value.
                    if (!variables.ContainsKey("conventions"))
                    {
                        variables["conventions"] = legacyConv;
                    }
                    variablesJsonVar.Set(context, JsonSerializer.Serialize(variables));
                }

                // Enable tools from input
                var enableTools = context.GetInput<bool?>("enableTools") ?? false;
                enableToolLoopVar.Set(context, enableTools);

                // Tenant ID for tenant-scoped prompt resolution (Story 27-6)
                tenantIdVar.Set(context, context.GetInput<string>("tenantId") ?? "");

                // Story 39-9 (D10) — additive/optional repair-ring inputs. Read
                // unconditionally so both the typed-role and legacy-InputJson paths
                // carry them; empty ⇒ no validation (the default).
                documentTypeVar.Set(context, context.GetInput<string>("documentType") ?? "");
                issueIdVar.Set(context, context.GetInput<string>("issueId") ?? "");

                var role = context.GetInput<string>("agentRole") ?? context.GetInput<string>("role");
                if (!string.IsNullOrWhiteSpace(role))
                {
                    taskPromptVar.Set(context, context.GetInput<string>("taskPrompt") ?? context.GetInput<string>("prompt") ?? "");
                    contextVar.Set(context, context.GetInput<string>("context") ?? "");
                    sessionIdVar.Set(context, context.GetInput<string>("sessionId") ?? "");
                    systemPromptOverrideVar.Set(context, context.GetInput<string>("systemPromptOverride") ?? "");

                    // Tool loop config from typed inputs
                    var loopConfigJson = context.GetInput<string>("toolLoopConfig") ?? "";
                    toolLoopConfigJsonVar.Set(context, loopConfigJson);

                    return role;
                }

                // Fall back to legacy InputJson
                var raw = context.GetInput<string>("InputJson") ?? "{}";
                inputVar.Set(context, raw);
                var input = ParseInput(raw);
                taskPromptVar.Set(context, input.UserPrompt);
                contextVar.Set(context, "");
                sessionIdVar.Set(context, input.CorrelationId ?? "");
                systemPromptOverrideVar.Set(context, input.SystemPromptOverride ?? "");

                // Tool loop config from legacy input
                enableToolLoopVar.Set(context, input.EnableToolLoop);
                if (input.ToolLoopConfig != null)
                    toolLoopConfigJsonVar.Set(context, JsonSerializer.Serialize(input.ToolLoopConfig));

                // Also check dict-style inputs (from BlockerDiagnosis etc.)
                var dictRole = context.GetInput<string>("role");
                if (!string.IsNullOrWhiteSpace(dictRole))
                {
                    var content = context.GetInput<string>("content") ?? "";
                    taskPromptVar.Set(context, content);
                    return dictRole;
                }

                // Default to a canonical AgentRole wire — the call-LLM endpoint
                // 422s on a non-canonical/unaliased role. (Empty-safe, not just
                // null-safe: input.Role is non-nullable and may be "".)
                return string.IsNullOrWhiteSpace(input.Role) ? "developer" : input.Role;
            })
        };
        initInputs.SetDisplayText("Initialize Inputs");

        // 1a. Resolve conventions from the convention store (Story 27-13).
        // Output feeds {{conventions}} in the prompt-render variables (the
        // store result takes precedence over any legacy passthrough value).
        // Empty-action callers bypass the store; see ResolveConventionsActivity.
        var resolveConventions = new ResolveConventionsActivity
        {
            Id = "ResolveConventions",
            Name = "Resolve Conventions",
            Role = new Input<string>(ctx => agentRoleVar.Get(ctx)),
            Action = new Input<string>(ctx => actionVar.Get(ctx)),
            TenantId = new Input<string>(ctx => tenantIdVar.Get(ctx)),
            LegacyConventions = new Input<string>(ctx => legacyConventionsVar.Get(ctx)),
            ResolvedConventions = new Output<string>(resolvedConventionsVar),
        };
        resolveConventions.SetDisplayText("Resolve Conventions");

        // 1a'. Merge the resolved conventions back into the variables JSON
        // so {{conventions}} renders the convention-store body (or the legacy
        // passthrough, when Action was empty). This runs unconditionally —
        // the resolve activity already chose the correct value.
        //
        // IMP-1 fix (post-review): the prior catch-all silently swallowed a
        // malformed variablesJsonVar by resetting the dict to empty — which
        // dropped role / workItemJson / every other variable a caller had
        // already wired in. A malformed state at this stage is a real fault
        // (the variables JSON was produced upstream by SerializeVariables in
        // the same workflow, so a parse failure means the upstream write is
        // broken). Rethrow with context as a TammaError so the workflow
        // engine surfaces the failure instead of running the LLM on a
        // stripped variable bag.
        // TODO(coverage): SetVariable lambdas can't be unit-tested cheaply
        // without a real ActivityExecutionContext; this fix is asserted
        // indirectly by the existing LlmCallWorkflow integration coverage.
        var mergeConventionsIntoVariables = new SetVariable
        {
            Id = "MergeConventions",
            Name = "Merge Conventions Into Variables",
            Variable = variablesJsonVar,
            Value = new(context => {
                var json = variablesJsonVar.Get(context) ?? "{}";
                var conventions = resolvedConventionsVar.Get(context) ?? "";
                Dictionary<string, object?> variables;
                try
                {
                    variables = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new();
                }
                catch (JsonException ex)
                {
                    // IMP-1: fail loud — a malformed VariablesJson at this
                    // stage is a real upstream bug (SerializeVariables wrote
                    // it). Swallowing it would silently strip every variable
                    // the caller wired in (role, workItemJson, …).
                    throw new Tamma.Core.TammaError(
                        "LLM.CONVENTIONS.MERGE.MALFORMED_VARIABLES_JSON",
                        $"MergeConventions could not parse upstream VariablesJson: {ex.Message}",
                        new Dictionary<string, object?>
                        {
                            ["jsonLength"] = json.Length,
                            ["conventionsLength"] = conventions.Length,
                        },
                        retryable: false,
                        severity: Tamma.Core.TammaErrorSeverity.High);
                }
                variables["conventions"] = conventions;
                return JsonSerializer.Serialize(variables);
            })
        };
        mergeConventionsIntoVariables.SetDisplayText("Merge Conventions");

        // 1b. Resolve prompt from registry (role + action → rendered prompt)
        var resolvePrompt = new ResolvePromptFromRegistryActivity
        {
            Id = "ResolvePrompt",
            Name = "Resolve Prompt",
            Role = new Input<string>(ctx => agentRoleVar.Get(ctx)),
            Action = new Input<string>(ctx => actionVar.Get(ctx)),
            VariablesJson = new Input<string>(ctx => variablesJsonVar.Get(ctx)),
            FallbackPrompt = new Input<string>(ctx => taskPromptVar.Get(ctx)),
            TenantId = new Input<string>(ctx => tenantIdVar.Get(ctx)),
            ResolvedPrompt = new Output<string>(taskPromptVar), // overrides taskPrompt with rendered template
            ResolvedSystemPrompt = new Output<string>(resolvedSystemPromptVar),
            EnableTools = new Output<bool>(enableToolLoopVar),
            MaxTokens = new Output<int>(registryMaxTokensVar),
        };
        resolvePrompt.SetDisplayText("Resolve Prompt");

        // 2. Parse input and set up budget.
        //
        // SCOPE (stated exactly, 2026-08-18): this seeds a PER-CALL bucket. CapUsd comes
        // from the caller's params.budgetCapUsd and SpentUsd starts at 0 on EVERY call, so
        // the CheckBudget gate below can only ever stop a single call from overrunning its
        // own cap — it never accumulates and is not a spend ceiling. Do not read it as one.
        //
        // The cumulative ceiling is owned server-side by RunningSpendBudgetGuard, which
        // every model call passes through: CallLlmInlineActivity is a thin client over
        // POST /api/v1/llm/call, and ManagedAgent.RunAsync consults that guard before the
        // provider call and fails the run closed with BUDGET_EXCEEDED. It reads the PERIOD
        // spend the API tracks, so it is the one check that can accumulate. Duplicating it
        // here would add an HTTP round-trip per provider attempt inside the engine hot path
        // and give two ceilings that can disagree — so this stays per-call by design.
        var setupBudget = new SetVariable
        {
            Id = "SetupBudget",
            Name = "Setup Budget",
            Variable = budgetStateVar,
            Value = new(context => {
                var raw = inputVar.Get(context);
                var input = ParseInput(raw);
                return JsonSerializer.Serialize(new BudgetState { CapUsd = input.BudgetCapUsd });
            })
        };
        setupBudget.SetDisplayText("Setup Budget");

        // 3. Resolve agent config from ELSA Agents DB (prompt, provider chain, settings)
        var resolveAgentConfig = WithLabel(new ResolveAgentConfigActivity
        {
            Id = "ResolveAgentConfig",
            Name = "Resolve Agent Config",
            AgentRoleProp = new(context => agentRoleVar.Get(context) ?? "assistant"),
            SystemPromptOverrideProp = new(context => systemPromptOverrideVar.Get(context))
        }, "Resolve Agent Config");

        // 4. Resolve provider chain — prefers: caller input > DB agent config >
        //    Llm:DefaultProviderChain config > hardcoded default. Precedence +
        //    filtering live in LlmProviderChainHelper (pure, unit-tested).
        //    2026-08-13: the filter now runs against the DI-configured
        //    ProviderAllowlist (defaults + Security:ProviderAllowlist:
        //    AdditionalProviders) instead of the static default instance,
        //    which silently ignored the very config key this node's rejection
        //    message told operators to set. That DI allowlist (plus the
        //    config-tier chain) is how the opt-in "scripted" provider becomes
        //    selectable for the engine-driven E2E — and how any self-hosted
        //    custom provider becomes selectable at all.
        var resolveChain = new SetVariable
        {
            Id = "ResolveChain",
            Name = "Resolve Provider Chain",
            Variable = providerChainVar,
            Value = new(context => {
                var raw = inputVar.Get(context);
                var input = ParseInput(raw);

                var dbChain = providerChainVar.Get(context) as ICollection<string>;

                var configuration = context.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                var configChain = Microsoft.Extensions.Configuration.ConfigurationBinder
                    .Get<string[]>(configuration.GetSection(Helpers.LlmProviderChainHelper.DefaultChainConfigKey));

                var allowlist = context.GetRequiredService<ProviderAllowlist>();

                var filtered = Helpers.LlmProviderChainHelper.Resolve(
                    input.ProviderChain, dbChain?.ToList(), configChain, allowlist);

                return (object)filtered;
            })
        };
        resolveChain.SetDisplayText("Resolve Provider Chain");

        // 4b. Check LLM concurrency — wait-loop until a slot opens
        var checkConcurrency = new CheckLlmConcurrencyActivity
        {
            Id = "CheckConcurrency",
            Name = "Check LLM Concurrency",
        };
        checkConcurrency.SetDisplayText("Check LLM Concurrency");

        // 4c. Delay before re-checking concurrency
        var concurrencyDelay = new ConcurrencyWaitDelayActivity
        {
            Id = "ConcurrencyDelay",
            Name = "Concurrency Wait",
        };
        concurrencyDelay.SetDisplayText("Concurrency Wait");

        // 5. ForEach provider in chain — reads from the resolved providerChainVar
        var forEachProviders = new ForEach<string>
        {
            Id = "ForEachProviderChain",
            Name = "For Each Provider",
            Items = new(context => {
                var chain = providerChainVar.Get(context);
                if (chain is ICollection<string> list && list.Count > 0)
                    return list;
                // Fallback (should not reach here since ResolveChain always sets a value)
                return (ICollection<string>)new List<string> { "anthropic", "openai", "openrouter" };
            }),
            Body = WithLabel(new Sequence
            {
                Id = "ProviderIterationBody",
                Name = "Provider Iteration",
                Activities =
                {
                    // ── Skip if already succeeded ──
                    WithLabel(new If
                    {
                        Id = "SkipIfSucceeded",
                        Name = "Already Succeeded?",
                        Condition = new(context => successVar.Get(context)),
                        Then = WithLabel(new Sequence { Id = "SkipNoop", Name = "Skip (No-op)", Activities = { /* skip */ } }, "Skip (No-op)"),
                        Else = WithLabel(new Sequence
                        {
                            Id = "TryProvider",
                            Name = "Try Provider",
                            Activities =
                            {
                                // Set current provider from the ForEach current value
                                WithLabel(new SetVariable
                                {
                                    Id = "SetCurrentProvider",
                                    Name = "Set Current Provider",
                                    Variable = currentProviderVar,
                                    Value = new(context => context.GetVariable<string>("CurrentValue") ?? "anthropic")
                                }, "Set Current Provider"),

                                // Reset attempt counter
                                WithLabel(new SetVariable
                                {
                                    Id = "ResetAttemptNumber",
                                    Name = "Reset Attempt",
                                    Variable = attemptNumberVar,
                                    Value = new(1)
                                }, "Reset Attempt"),

                                // ── 3a. Check circuit breaker ──
                                WithLabel(new If
                                {
                                    Id = "CheckCircuitBreaker",
                                    Name = "Circuit Breaker Open?",
                                    Condition = new(context => {
                                        var provider = currentProviderVar.Get(context);
                                        var statesJson = circuitBreakerStatesVar.Get(context);
                                        return IsCircuitBreakerOpen(provider, statesJson);
                                    }),
                                    // Circuit breaker is OPEN → skip this provider
                                    Then = WithLabel(new Sequence
                                    {
                                        Id = "RecordCBSkip",
                                        Name = "Record CB Skip",
                                        Activities =
                                        {
                                            WithLabel(new SetVariable
                                            {
                                                Id = "DiagCBSkip",
                                                Name = "Diag: CB Skip",
                                                Variable = diagnosticsListVar,
                                                Value = new(context => {
                                                    var list = DeserializeList<ProviderAttemptDiagnostic>(diagnosticsListVar.Get(context));
                                                    var newList = new List<ProviderAttemptDiagnostic>(list);
                                                    newList.Add(new ProviderAttemptDiagnostic
                                                    {
                                                        ProviderName = currentProviderVar.Get(context) ?? "",
                                                        AttemptNumber = 0,
                                                        Succeeded = false,
                                                        CircuitBreakerSkipped = true,
                                                        StartedAtUtc = DateTime.UtcNow,
                                                        ErrorMessage = "Circuit breaker is open"
                                                    });
                                                    return JsonSerializer.Serialize(newList);
                                                })
                                            }, "Diag: CB Skip")
                                        }
                                    }, "Record CB Skip"),
                                    // Circuit breaker is CLOSED or HALF_OPEN → proceed
                                    Else = WithLabel(new Sequence
                                    {
                                        Id = "CBClosed",
                                        Name = "CB Closed",
                                        Activities =
                                        {
                                            // ── 3b. Check the PER-CALL budget ──
                                            // Per-call only — see SetupBudget above for why,
                                            // and for where the cumulative ceiling actually
                                            // lives (RunningSpendBudgetGuard, server-side).
                                            WithLabel(new If
                                            {
                                                Id = "CheckBudget",
                                                Name = "Budget Exhausted?",
                                                Condition = new(context => {
                                                    var budgetJson = budgetStateVar.Get(context);
                                                    return IsBudgetExhausted(budgetJson);
                                                }),
                                                // Budget exhausted → skip
                                                Then = WithLabel(new Sequence
                                                {
                                                    Id = "RecordBudgetSkip",
                                                    Name = "Record Budget Skip",
                                                    Activities =
                                                    {
                                                        WithLabel(new SetVariable
                                                        {
                                                            Id = "DiagBudgetSkip",
                                                            Name = "Diag: Budget Skip",
                                                            Variable = diagnosticsListVar,
                                                            Value = new(context => {
                                                                var list = DeserializeList<ProviderAttemptDiagnostic>(diagnosticsListVar.Get(context));
                                                                var newList = new List<ProviderAttemptDiagnostic>(list);
                                                                newList.Add(new ProviderAttemptDiagnostic
                                                                {
                                                                    ProviderName = currentProviderVar.Get(context) ?? "",
                                                                    AttemptNumber = 0,
                                                                    Succeeded = false,
                                                                    BudgetExhausted = true,
                                                                    StartedAtUtc = DateTime.UtcNow,
                                                                    ErrorMessage = "Budget exhausted"
                                                                });
                                                                return JsonSerializer.Serialize(newList);
                                                            })
                                                        }, "Diag: Budget Skip")
                                                    }
                                                }, "Record Budget Skip"),
                                                // Budget OK → resolve tools and call
                                                // (System prompt is already resolved by ResolveAgentConfigActivity)
                                                Else = WithLabel(new Sequence
                                                {
                                                    Id = "BudgetOk",
                                                    Name = "Budget OK",
                                                    Activities =
                                                    {
                                                        // ── Resolve tools ──
                                                        WithLabel(new SetVariable
                                                        {
                                                            Id = "ResolveTools",
                                                            Name = "Resolve Tools",
                                                            Variable = resolvedToolsJsonVar,
                                                            Value = new(context => {
                                                                var raw = inputVar.Get(context);
                                                                var input = ParseInput(raw);
                                                                if (input.ToolNames == null || input.ToolNames.Count == 0)
                                                                    return "";
                                                                return JsonSerializer.Serialize(input.ToolNames);
                                                            })
                                                        }, "Resolve Tools"),

                                                        // ── Call LLM with retry loop ──
                                                        BuildRetryLoop(
                                                            inputVar,
                                                            taskPromptVar,
                                                            agentRoleVar,
                                                            actionVar,
                                                            variablesJsonVar,
                                                            registryMaxTokensVar,
                                                            currentProviderVar,
                                                            resolvedSystemPromptVar,
                                                            resolvedToolsJsonVar,
                                                            attemptNumberVar,
                                                            maxRetriesVar,
                                                            successVar,
                                                            lastDiagnosticVar,
                                                            lastResponseVar,
                                                            diagnosticsListVar,
                                                            circuitBreakerStatesVar,
                                                            budgetStateVar,
                                                            workflowOutputVar,
                                                            enableToolLoopVar,
                                                            toolLoopConfigJsonVar,
                                                            tenantIdVar,
                                                            documentTypeVar,
                                                            issueIdVar)
                                                    }
                                                }, "Budget OK")
                                            }, "Budget Exhausted?")
                                        }
                                    }, "CB Closed")
                                }, "Circuit Breaker Open?")
                            }
                        }, "Try Provider")
                    }, "Already Succeeded?")
                }
            }, "Provider Iteration")
        };
        forEachProviders.SetDisplayText("For Each Provider");

        // 5. Check if call succeeded
        var failureCheck = new FlowDecision(context => successVar.Get(context))
        {
            Id = "FailureCheck",
            Name = "Call Succeeded?"
        };
        failureCheck.SetDisplayText("Call Succeeded?");

        // 5a. Build failure output (all providers failed)
        var buildFailureOutput = new SetVariable
        {
            Id = "BuildFailureOutput",
            Name = "Build Failure Output",
            Variable = workflowOutputVar,
            Value = new(context => {
                var diagnostics = DeserializeList<ProviderAttemptDiagnostic>(diagnosticsListVar.Get(context));
                var output = new LlmCallWorkflowOutput
                {
                    Success = false,
                    ErrorMessage = "All providers in the chain failed",
                    TotalDurationMs = diagnostics.Sum(d => d.DurationMs),
                    Diagnostics = diagnostics
                };
                return JsonSerializer.Serialize(output, SerializerOptions);
            })
        };
        buildFailureOutput.SetDisplayText("Build Failure Output");

        // 6. Set workflow outputs for parent consumption
        var setOutputs = new Sequence
        {
            Id = "SetOutputs",
            Name = "Set Outputs",
            Activities =
            {
                WithLabel(new SetOutput
                {
                    Id = "OutputSuccess",
                    Name = "Output: success",
                    OutputName = new("success"),
                    OutputValue = new(context => (object)successVar.Get(context))
                }, "Output: success"),
                WithLabel(new SetOutput
                {
                    Id = "OutputWorkflowOutput",
                    Name = "Output: workflowOutput",
                    OutputName = new("workflowOutput"),
                    OutputValue = new(context => (object)(workflowOutputVar.Get(context) ?? "{}"))
                }, "Output: workflowOutput"),
                WithLabel(new SetOutput
                {
                    Id = "OutputLlmResponse",
                    Name = "Output: llmResponse",
                    OutputName = new("llmResponse"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.ResponseText ?? "");
                    })
                }, "Output: llmResponse"),
                WithLabel(new SetOutput
                {
                    Id = "OutputProviderUsed",
                    Name = "Output: providerUsed",
                    OutputName = new("providerUsed"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.SuccessfulProvider ?? "");
                    })
                }, "Output: providerUsed"),
                WithLabel(new SetOutput
                {
                    Id = "OutputCostUsd",
                    Name = "Output: costUsd",
                    OutputName = new("costUsd"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.EstimatedCostUsd ?? 0m);
                    })
                }, "Output: costUsd"),
                WithLabel(new SetOutput
                {
                    Id = "OutputTokensUsed",
                    Name = "Output: tokensUsed",
                    OutputName = new("tokensUsed"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.TotalTokens ?? 0);
                    })
                }, "Output: tokensUsed"),
                WithLabel(new SetOutput
                {
                    Id = "OutputToolLoopTokens",
                    Name = "Output: toolLoopTokens",
                    OutputName = new("toolLoopTokens"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.ToolLoopTokens ?? 0);
                    })
                }, "Output: toolLoopTokens"),
                WithLabel(new SetOutput
                {
                    Id = "OutputToolLoopTurns",
                    Name = "Output: toolLoopTurns",
                    OutputName = new("toolLoopTurns"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.ToolLoopTurns ?? 0);
                    })
                }, "Output: toolLoopTurns"),
                WithLabel(new SetOutput
                {
                    Id = "OutputToolLoopExhausted",
                    Name = "Output: toolLoopExhausted",
                    OutputName = new("toolLoopExhausted"),
                    OutputValue = new(context =>
                    {
                        var outputJson = workflowOutputVar.Get(context);
                        var output = SafeDeserialize<LlmCallWorkflowOutput>(outputJson);
                        return (object)(output?.ToolLoopExhausted ?? false);
                    })
                }, "Output: toolLoopExhausted"),
                // Story 39-9 (D10) — surface the content-validation wire block back to
                // the caller (empty when no validator ran). Additive output.
                WithLabel(new SetOutput
                {
                    Id = "OutputContentValidation",
                    Name = "Output: contentValidation",
                    OutputName = new("contentValidation"),
                    OutputValue = new(context => (object)(contentValidationVar.Get(context) ?? ""))
                }, "Output: contentValidation")
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        // ============================================================
        // Flowchart
        // ============================================================
        builder.Root = new Flowchart
        {
            Id = "LlmCallFlowchart",
            Start = initInputs,
            Activities =
            {
                initInputs, resolveConventions, mergeConventionsIntoVariables,
                resolvePrompt, setupBudget, resolveAgentConfig, resolveChain,
                checkConcurrency, concurrencyDelay,
                forEachProviders, failureCheck, buildFailureOutput, setOutputs
            },
            Connections =
            {
                // InitInputs → Resolve Conventions → Merge Conventions → Resolve Prompt → Setup Budget
                Connect(initInputs, resolveConventions),
                Connect(resolveConventions, mergeConventionsIntoVariables),
                Connect(mergeConventionsIntoVariables, resolvePrompt),
                Connect(resolvePrompt, setupBudget),

                // Setup Budget → Resolve Agent Config (DB lookup)
                Connect(setupBudget, resolveAgentConfig),

                // Resolve Agent Config → Resolve Provider Chain
                Connect(resolveAgentConfig, resolveChain),

                // Resolve Provider Chain → Check Concurrency
                Connect(resolveChain, checkConcurrency),

                // Check Concurrency → [OK] → For Each Provider
                ConnectOutcome(checkConcurrency, "OK", forEachProviders),

                // Check Concurrency → [AtLimit] → Delay → re-check (loop)
                ConnectOutcome(checkConcurrency, "AtLimit", concurrencyDelay),
                Connect(concurrencyDelay, checkConcurrency),

                // For Each Provider → Call Succeeded?
                Connect(forEachProviders, failureCheck),

                // Call Succeeded? Yes → Set Outputs
                ConnectOutcome(failureCheck, "True", setOutputs),

                // Call Succeeded? No → Build Failure Output → Set Outputs
                ConnectOutcome(failureCheck, "False", buildFailureOutput),
                Connect(buildFailureOutput, setOutputs)
            }
        };
    }

    /// <summary>
    /// Builds a retry loop for calling a single LLM provider.
    /// Uses a While activity that retries on transient failures up to MaxRetries.
    /// </summary>
    private static While BuildRetryLoop(
        Variable<string> inputVar,
        Variable<string> taskPromptVar,
        Variable<string> agentRoleVar,
        Variable<string> actionVar,
        Variable<string> variablesJsonVar,
        Variable<int> registryMaxTokensVar,
        Variable<string> currentProviderVar,
        Variable<string> resolvedSystemPromptVar,
        Variable<string> resolvedToolsJsonVar,
        Variable<int> attemptNumberVar,
        Variable<int> maxRetriesVar,
        Variable<bool> successVar,
        Variable<string> lastDiagnosticVar,
        Variable<string> lastResponseVar,
        Variable<string> diagnosticsListVar,
        Variable<string> circuitBreakerStatesVar,
        Variable<string> budgetStateVar,
        Variable<string> workflowOutputVar,
        Variable<bool> enableToolLoopVar,
        Variable<string> toolLoopConfigJsonVar,
        Variable<string> tenantIdVar,
        Variable<string> documentTypeVar,
        Variable<string> issueIdVar)
    {
        var whileLoop = new While((string?)null);
        whileLoop.Id = "RetryLoop";
        whileLoop.Name = "Retry Loop";
        whileLoop.SetDisplayText("Retry Loop");
        whileLoop.Condition = new Input<bool>(context =>
            !successVar.Get(context) &&
            attemptNumberVar.Get(context) <= maxRetriesVar.Get(context));

        // Build the retry loop body
        var retryCheckIf = new If
        {
            Id = "RetryCheck",
            Name = "Transient Error?",
            Condition = new(context =>
            {
                var diagJson = context.GetVariable<string>("LastDiagnostic") ?? "";
                if (string.IsNullOrWhiteSpace(diagJson)) return false;
                try
                {
                    var diag = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(diagJson);
                    if (diag == null) return false;
                    var code = diag.HttpStatusCode;
                    return code == 429 || code == 502 || code == 503 || code == 504 || code == 0;
                }
                catch { return false; }
            }),
            Then = WithLabel(new SetVariable
            {
                Id = "IncrementAttempt",
                Name = "Increment Attempt",
                Variable = attemptNumberVar,
                Value = new(context => attemptNumberVar.Get(context) + 1)
            }, "Increment Attempt"),
            Else = WithLabel(new SetVariable
            {
                Id = "ExhaustAttempts",
                Name = "Exhaust Attempts",
                Variable = attemptNumberVar,
                Value = new(context => maxRetriesVar.Get(context) + 1)
            }, "Exhaust Attempts")
        };
        retryCheckIf.SetDisplayText("Transient Error?");

        var successCheckIf = new If
        {
            Id = "SuccessCheck",
            Name = "LLM Succeeded?",
            Condition = new(context =>
            {
                var diagJson = context.GetVariable<string>("LastDiagnostic") ?? "";
                if (string.IsNullOrWhiteSpace(diagJson)) return false;
                try
                {
                    var diag = JsonSerializer.Deserialize<ProviderAttemptDiagnostic>(diagJson);
                    return diag?.Succeeded == true;
                }
                catch { return false; }
            }),
            Then = WithLabel(new Sequence
            {
                Id = "RecordSuccess",
                Name = "Record Success",
                Activities =
                {
                    WithLabel(new SetVariable
                    {
                        Id = "SetSuccessTrue",
                        Name = "Set Success",
                        Variable = successVar,
                        Value = new(true)
                    }, "Set Success"),
                    WithLabel(new SetVariable
                    {
                        Id = "BuildSuccessOutput",
                        Name = "Build Success Output",
                        Variable = workflowOutputVar,
                        Value = new(context =>
                        {
                            var respJson = context.GetVariable<string>("LastResponse") ?? "{}";
                            var resp = SafeDeserialize<NormalizedLlmResponse>(respJson);
                            var budgetJson2 = budgetStateVar.Get(context);
                            var budget = SafeDeserialize<BudgetState>(budgetJson2);
                            var allDiags = DeserializeList<ProviderAttemptDiagnostic>(diagnosticsListVar.Get(context));

                            // Read tool loop output variables (set by CallLlmInlineActivity when EnableToolLoop=true)
                            var toolLoopTokens = context.GetVariable<int?>("ToolLoopTokens") ?? 0;
                            var toolLoopTurns = context.GetVariable<int?>("ToolLoopTurns") ?? 0;
                            var toolLoopExhausted = context.GetVariable<bool?>("ToolLoopExhausted") ?? false;

                            var output = new LlmCallWorkflowOutput
                            {
                                Success = true,
                                ResponseText = resp?.ResponseText,
                                SuccessfulProvider = currentProviderVar.Get(context),
                                ModelUsed = resp?.Model,
                                PromptTokens = resp?.PromptTokens ?? 0,
                                CompletionTokens = resp?.CompletionTokens ?? 0,
                                TotalTokens = (resp?.PromptTokens ?? 0) + (resp?.CompletionTokens ?? 0),
                                EstimatedCostUsd = budget?.SpentUsd ?? 0,
                                TotalDurationMs = allDiags.Sum(d => d.DurationMs),
                                Diagnostics = allDiags,
                                ToolCalls = resp?.ToolCalls,
                                ToolLoopTokens = toolLoopTokens,
                                ToolLoopTurns = toolLoopTurns,
                                ToolLoopExhausted = toolLoopExhausted
                            };
                            return JsonSerializer.Serialize(output, SerializerOptions);
                        })
                    }, "Build Success Output")
                }
            }, "Record Success"),
            Else = WithLabel(new Sequence
            {
                Id = "HandleRetry",
                Name = "Handle Retry",
                Activities = { retryCheckIf }
            }, "Handle Retry")
        };
        successCheckIf.SetDisplayText("LLM Succeeded?");

        var loopBody = new Sequence
        {
            Id = "RetryLoopBody",
            Name = "Retry Loop Body",
            Activities =
            {
                WithLabel(new CallLlmInlineActivity
                {
                    Id = "CallLlm",
                    Name = "Call LLM",
                    InputJsonProp = new(context => inputVar.Get(context)),
                    ProviderNameProp = new(context => currentProviderVar.Get(context)),
                    SystemPromptProp = new(context => resolvedSystemPromptVar.Get(context)),
                    ToolsJsonProp = new(context => resolvedToolsJsonVar.Get(context)),
                    AttemptNumberProp = new(context => attemptNumberVar.Get(context)),
                    EnableToolLoopProp = new(context => enableToolLoopVar.Get(context)),
                    ToolLoopConfigJsonProp = new(context => toolLoopConfigJsonVar.Get(context)),
                    // Story 32-3 (AC3) — thread the tenant id for BYOK credential
                    // resolution (same pattern as the prompt/convention steps).
                    TenantIdProp = new(context => tenantIdVar.Get(context)),
                    // Typed-dispatch fix — thread the registry-rendered prompt +
                    // role/action/variables + registry MaxTokens so the wire
                    // request carries them on typed dispatches (where InputJson
                    // is empty). BuildLlmCallRequest prefers these when present
                    // and keeps the legacy InputJson mapping otherwise.
                    AgentRoleProp = new(context => agentRoleVar.Get(context)),
                    ActionProp = new(context => actionVar.Get(context)),
                    RenderedPromptProp = new(context => taskPromptVar.Get(context)),
                    VariablesJsonProp = new(context => variablesJsonVar.Get(context)),
                    RegistryMaxTokensProp = new(context => registryMaxTokensVar.Get(context)),
                    // Story 39-9 (D10) — thread the additive/optional repair-ring inputs.
                    DocumentTypeProp = new(context => documentTypeVar.Get(context)),
                    IssueIdProp = new(context => issueIdVar.Get(context))
                }, "Call LLM"),
                WithLabel(new RecordDiagnosticsInlineActivity
                {
                    Id = "RecordDiagnostics",
                    Name = "Record Diagnostics",
                    ProviderNameProp = new(context => currentProviderVar.Get(context)),
                    DiagnosticsListJsonProp = new(context => diagnosticsListVar.Get(context)),
                    CircuitBreakerStatesJsonProp = new(context => circuitBreakerStatesVar.Get(context)),
                    BudgetStateJsonProp = new(context => budgetStateVar.Get(context))
                }, "Record Diagnostics"),
                successCheckIf
            }
        };
        loopBody.SetDisplayText("Retry Loop Body");

        whileLoop.Body = loopBody;
        return whileLoop;
    }

    // ================================================================
    // Flowchart helpers
    // ================================================================

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

    // ================================================================
    // Helper methods (static, used in expression lambdas)
    // ================================================================

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static LlmCallWorkflowInput ParseInput(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new LlmCallWorkflowInput();

        try
        {
            return JsonSerializer.Deserialize<LlmCallWorkflowInput>(json) ?? new LlmCallWorkflowInput();
        }
        catch
        {
            return new LlmCallWorkflowInput();
        }
    }

    private static bool IsCircuitBreakerOpen(string? provider, string? statesJson)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(statesJson))
            return false;

        try
        {
            var states = JsonSerializer.Deserialize<Dictionary<string, CircuitBreakerState>>(statesJson);
            if (states == null || !states.TryGetValue(provider, out var state))
                return false;

            if (state.Status == CircuitBreakerStatus.Open)
            {
                // Check if cooldown has elapsed
                if (state.OpenedAtUtc.HasValue &&
                    DateTime.UtcNow - state.OpenedAtUtc.Value >= state.CooldownPeriod)
                {
                    return false; // Cooldown elapsed, allow half-open probe
                }
                return true; // Still open
            }

            return false;
        }
        catch
        {
            // SECURITY FIX: Fail closed. If we can't check the circuit breaker,
            // deny the request rather than allowing it through a broken safety check.
            return true;
        }
    }

    private static bool IsBudgetExhausted(string? budgetJson)
    {
        if (string.IsNullOrWhiteSpace(budgetJson)) return false;

        try
        {
            var budget = JsonSerializer.Deserialize<BudgetState>(budgetJson);
            return budget?.IsExhausted == true;
        }
        catch
        {
            // SECURITY FIX: Fail closed. If we can't check the budget,
            // deny the request rather than allowing unchecked spending.
            return true;
        }
    }

    private static List<T> DeserializeList<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<T>();

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }
}
