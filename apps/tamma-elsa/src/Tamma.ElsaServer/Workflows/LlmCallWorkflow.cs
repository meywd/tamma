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
///   InitInputs → SetupBudget → ResolveAgentConfig → ResolveChain → ForEachProviderChain
///     → FailureCheck → [success?]
///       Yes → SetOutputs
///       No  → BuildFailureOutput → SetOutputs
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
        var agentRoleVar = builder.WithVariable<string>("AgentRole", "assistant");
        var taskPromptVar = builder.WithVariable<string>("TaskPrompt", "");
        var contextVar = builder.WithVariable<string>("Context", "");
        var sessionIdVar = builder.WithVariable<string>("SessionId", "");

        // Legacy input support
        var inputVar = builder.WithVariable<string>("InputJson", "");

        // State variables
        var circuitBreakerStatesVar = builder.WithVariable<string>("CircuitBreakerStatesJson", "{}");
        var budgetStateVar = builder.WithVariable<string>("BudgetStateJson", "{}");
        var diagnosticsListVar = builder.WithVariable<string>("DiagnosticsListJson", "[]");
        var workflowOutputVar = builder.WithVariable<string>("WorkflowOutputJson", "");
        var successVar = builder.WithVariable<bool>("CallSucceeded", false);
        var currentProviderVar = builder.WithVariable<string>("CurrentProvider", "");
        var lastDiagnosticVar = builder.WithVariable<string>("LastDiagnostic", "");
        var lastResponseVar = builder.WithVariable<string>("LastResponse", "");
        var attemptNumberVar = builder.WithVariable<int>("AttemptNumber", 1);
        var maxRetriesVar = builder.WithVariable<int>("MaxRetries", 3);
        var providerChainVar = builder.WithVariable<object>("ProviderChain", new List<string>());
        var systemPromptOverrideVar = builder.WithVariable<string>("SystemPromptOverride", "");
        var resolvedSystemPromptVar = builder.WithVariable<string>("ResolvedSystemPrompt", "");
        var resolvedToolsJsonVar = builder.WithVariable<string>("ResolvedToolsJson", "");

        // Tool loop variables
        var enableToolLoopVar = builder.WithVariable<bool>("EnableToolLoop", false);
        var toolLoopConfigJsonVar = builder.WithVariable<string>("ToolLoopConfigJson", "");
        var toolLoopTokensVar = builder.WithVariable<int>("ToolLoopTokens", 0);
        var toolLoopTurnsVar = builder.WithVariable<int>("ToolLoopTurns", 0);
        var toolLoopExhaustedVar = builder.WithVariable<bool>("ToolLoopExhausted", false);

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
                var role = context.GetInput<string>("agentRole");
                if (!string.IsNullOrWhiteSpace(role))
                {
                    taskPromptVar.Set(context, context.GetInput<string>("taskPrompt") ?? "");
                    contextVar.Set(context, context.GetInput<string>("context") ?? "");
                    sessionIdVar.Set(context, context.GetInput<string>("sessionId") ?? "");
                    systemPromptOverrideVar.Set(context, context.GetInput<string>("systemPromptOverride") ?? "");

                    // Tool loop config from typed inputs
                    var enableLoop = context.GetInput<bool?>("enableToolLoop") ?? false;
                    enableToolLoopVar.Set(context, enableLoop);
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

                return input.Role ?? "assistant";
            })
        };
        initInputs.SetDisplayText("Initialize Inputs");

        // 2. Parse input and set up budget
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

        // 4. Resolve provider chain — prefers: caller input > DB agent config > default
        var resolveChain = new SetVariable
        {
            Id = "ResolveChain",
            Name = "Resolve Provider Chain",
            Variable = providerChainVar,
            Value = new(context => {
                var raw = inputVar.Get(context);
                var input = ParseInput(raw);

                List<string> chain;

                // Priority 1: Caller provided an explicit chain in input
                if (input.ProviderChain.Count > 0)
                    chain = input.ProviderChain;
                // Priority 2: Agent config from DB set a chain (via ResolveAgentConfigActivity)
                else if (providerChainVar.Get(context) is ICollection<string> dbChain && dbChain.Count > 0)
                    chain = dbChain.ToList();
                // Priority 3: Default chain
                else
                    chain = new List<string> { "anthropic", "openai", "openrouter" };

                // Filter through provider allowlist
                var filtered = ProviderAllowlist.FilterAllowedDefault(chain);
                if (filtered.Count == 0)
                {
                    // All providers rejected — fall back to default allowed providers
                    filtered = new List<string> { "anthropic", "openai", "openrouter" };
                }

                return (object)filtered;
            })
        };
        resolveChain.SetDisplayText("Resolve Provider Chain");

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
                                            // ── 3b. Check budget ──
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
                                                            toolLoopConfigJsonVar)
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
                }, "Output: toolLoopExhausted")
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
                initInputs, setupBudget, resolveAgentConfig, resolveChain,
                forEachProviders, failureCheck, buildFailureOutput, setOutputs
            },
            Connections =
            {
                // InitInputs → Setup Budget
                Connect(initInputs, setupBudget),

                // Setup Budget → Resolve Agent Config (DB lookup)
                Connect(setupBudget, resolveAgentConfig),

                // Resolve Agent Config → Resolve Provider Chain
                Connect(resolveAgentConfig, resolveChain),

                // Resolve Provider Chain → For Each Provider
                Connect(resolveChain, forEachProviders),

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
        Variable<string> toolLoopConfigJsonVar)
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
                    ToolLoopConfigJsonProp = new(context => toolLoopConfigJsonVar.Get(context))
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
