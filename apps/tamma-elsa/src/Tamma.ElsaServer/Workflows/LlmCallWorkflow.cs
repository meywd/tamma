using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Tamma.Activities.LlmCall;
using Tamma.Activities.LlmCall.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

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
///   InitInputs → SetupBudget → ResolveChain → ForEachProviderChain
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
        var resolvedSystemPromptVar = builder.WithVariable<string>("ResolvedSystemPrompt", "");
        var resolvedToolsJsonVar = builder.WithVariable<string>("ResolvedToolsJson", "");

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
                    return role;
                }

                // Fall back to legacy InputJson
                var raw = context.GetInput<string>("InputJson") ?? "{}";
                inputVar.Set(context, raw);
                var input = ParseInput(raw);
                taskPromptVar.Set(context, input.UserPrompt);
                contextVar.Set(context, "");
                sessionIdVar.Set(context, input.CorrelationId ?? "");

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

        // 3. Resolve provider chain from config
        var resolveChain = new SetVariable
        {
            Id = "ResolveChain",
            Name = "Resolve Provider Chain",
            Variable = providerChainVar,
            Value = new(context => {
                var role = agentRoleVar.Get(context) ?? "default";
                var raw = inputVar.Get(context);
                var input = ParseInput(raw);

                // If caller provided an explicit chain, use it
                if (input.ProviderChain.Count > 0)
                    return (object)input.ProviderChain;

                // Otherwise resolve from config: AgentsConfig:ProviderChains:{role}
                // Config isn't available in expression lambdas, so fall back to default
                var chain = new List<string> { "anthropic", "openai", "openrouter" };
                return (object)chain;
            })
        };

        // 4. ForEach provider in chain — kept as a Sequence-bodied ForEach (composite activity)
        var forEachProviders = new ForEach<string>
        {
            Id = "ForEachProviderChain",
            Name = "For Each Provider",
            Items = new(context => {
                var role = agentRoleVar.Get(context) ?? "default";
                var raw = inputVar.Get(context);
                var input = ParseInput(raw);

                if (input.ProviderChain.Count > 0)
                    return (ICollection<string>)input.ProviderChain;

                return (ICollection<string>)new List<string> { "anthropic", "openai", "openrouter" };
            }),
            Body = new Sequence
            {
                Id = "ProviderIterationBody",
                Name = "Provider Iteration",
                Activities =
                {
                    // ── Skip if already succeeded ──
                    new If
                    {
                        Id = "SkipIfSucceeded",
                        Name = "Already Succeeded?",
                        Condition = new(context => successVar.Get(context)),
                        Then = new Sequence { Id = "SkipNoop", Name = "Skip (No-op)", Activities = { /* skip */ } },
                        Else = new Sequence
                        {
                            Id = "TryProvider",
                            Name = "Try Provider",
                            Activities =
                            {
                                // Set current provider from the ForEach current value
                                new SetVariable
                                {
                                    Id = "SetCurrentProvider",
                                    Name = "Set Current Provider",
                                    Variable = currentProviderVar,
                                    Value = new(context => context.GetVariable<string>("CurrentValue") ?? "anthropic")
                                },

                                // Reset attempt counter
                                new SetVariable
                                {
                                    Id = "ResetAttemptNumber",
                                    Name = "Reset Attempt",
                                    Variable = attemptNumberVar,
                                    Value = new(1)
                                },

                                // ── 3a. Check circuit breaker ──
                                new If
                                {
                                    Id = "CheckCircuitBreaker",
                                    Name = "Circuit Breaker Open?",
                                    Condition = new(context => {
                                        var provider = currentProviderVar.Get(context);
                                        var statesJson = circuitBreakerStatesVar.Get(context);
                                        return IsCircuitBreakerOpen(provider, statesJson);
                                    }),
                                    // Circuit breaker is OPEN → skip this provider
                                    Then = new Sequence
                                    {
                                        Id = "RecordCBSkip",
                                        Name = "Record CB Skip",
                                        Activities =
                                        {
                                            new SetVariable
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
                                            }
                                        }
                                    },
                                    // Circuit breaker is CLOSED or HALF_OPEN → proceed
                                    Else = new Sequence
                                    {
                                        Id = "CBClosed",
                                        Name = "CB Closed",
                                        Activities =
                                        {
                                            // ── 3b. Check budget ──
                                            new If
                                            {
                                                Id = "CheckBudget",
                                                Name = "Budget Exhausted?",
                                                Condition = new(context => {
                                                    var budgetJson = budgetStateVar.Get(context);
                                                    return IsBudgetExhausted(budgetJson);
                                                }),
                                                // Budget exhausted → skip
                                                Then = new Sequence
                                                {
                                                    Id = "RecordBudgetSkip",
                                                    Name = "Record Budget Skip",
                                                    Activities =
                                                    {
                                                        new SetVariable
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
                                                        }
                                                    }
                                                },
                                                // Budget OK → resolve prompt and call
                                                Else = new Sequence
                                                {
                                                    Id = "BudgetOk",
                                                    Name = "Budget OK",
                                                    Activities =
                                                    {
                                                        // ── 3c. Resolve prompt ──
                                                        new SetVariable
                                                        {
                                                            Id = "ResolvePrompt",
                                                            Name = "Resolve Prompt",
                                                            Variable = resolvedSystemPromptVar,
                                                            Value = new(context => {
                                                                var raw = inputVar.Get(context);
                                                                var input = ParseInput(raw);
                                                                if (!string.IsNullOrWhiteSpace(input.SystemPromptOverride))
                                                                    return input.SystemPromptOverride;
                                                                var role = agentRoleVar.Get(context) ?? "assistant";
                                                                return GetRolePrompt(role);
                                                            })
                                                        },

                                                        // ── 3d. Resolve tools ──
                                                        new SetVariable
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
                                                        },

                                                        // ── 3e. Call LLM with retry loop ──
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
                                                            workflowOutputVar)
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        // 5. Check if call succeeded
        var failureCheck = new FlowDecision(context => successVar.Get(context))
        {
            Id = "FailureCheck",
            Name = "Call Succeeded?"
        };

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

        // 6. Set workflow outputs for parent consumption
        var setOutputs = new Sequence
        {
            Id = "SetOutputs",
            Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                {
                    Id = "OutputSuccess",
                    Name = "Output: success",
                    OutputName = new("success"),
                    OutputValue = new(context => (object)successVar.Get(context))
                },
                new SetOutput
                {
                    Id = "OutputWorkflowOutput",
                    Name = "Output: workflowOutput",
                    OutputName = new("workflowOutput"),
                    OutputValue = new(context => (object)(workflowOutputVar.Get(context) ?? "{}"))
                },
                new SetOutput
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
                },
                new SetOutput
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
                },
                new SetOutput
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
                },
                new SetOutput
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
                }
            }
        };

        // ============================================================
        // Flowchart
        // ============================================================
        builder.Root = new Flowchart
        {
            Id = "LlmCallFlowchart",
            Start = initInputs,
            Activities =
            {
                initInputs, setupBudget, resolveChain, forEachProviders,
                failureCheck, buildFailureOutput, setOutputs
            },
            Connections =
            {
                // InitInputs → Setup Budget
                Connect(initInputs, setupBudget),

                // Setup Budget → Resolve Provider Chain
                Connect(setupBudget, resolveChain),

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
        Variable<string> workflowOutputVar)
    {
        var whileLoop = new While((string?)null);
        whileLoop.Id = "RetryLoop";
        whileLoop.Name = "Retry Loop";
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
            Then = new SetVariable
            {
                Id = "IncrementAttempt",
                Name = "Increment Attempt",
                Variable = attemptNumberVar,
                Value = new(context => attemptNumberVar.Get(context) + 1)
            },
            Else = new SetVariable
            {
                Id = "ExhaustAttempts",
                Name = "Exhaust Attempts",
                Variable = attemptNumberVar,
                Value = new(context => maxRetriesVar.Get(context) + 1)
            }
        };

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
            Then = new Sequence
            {
                Id = "RecordSuccess",
                Name = "Record Success",
                Activities =
                {
                    new SetVariable
                    {
                        Id = "SetSuccessTrue",
                        Name = "Set Success",
                        Variable = successVar,
                        Value = new(true)
                    },
                    new SetVariable
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
                                ToolCalls = resp?.ToolCalls
                            };
                            return JsonSerializer.Serialize(output, SerializerOptions);
                        })
                    }
                }
            },
            Else = new Sequence
            {
                Id = "HandleRetry",
                Name = "Handle Retry",
                Activities = { retryCheckIf }
            }
        };

        var loopBody = new Sequence
        {
            Id = "RetryLoopBody",
            Name = "Retry Loop Body",
            Activities =
            {
                new CallLlmInlineActivity
                {
                    Id = "CallLlm",
                    Name = "Call LLM",
                    InputJsonProp = new(context => inputVar.Get(context)),
                    ProviderNameProp = new(context => currentProviderVar.Get(context)),
                    SystemPromptProp = new(context => resolvedSystemPromptVar.Get(context)),
                    ToolsJsonProp = new(context => resolvedToolsJsonVar.Get(context)),
                    AttemptNumberProp = new(context => attemptNumberVar.Get(context))
                },
                new RecordDiagnosticsInlineActivity
                {
                    Id = "RecordDiagnostics",
                    Name = "Record Diagnostics",
                    ProviderNameProp = new(context => currentProviderVar.Get(context)),
                    DiagnosticsListJsonProp = new(context => diagnosticsListVar.Get(context)),
                    CircuitBreakerStatesJsonProp = new(context => circuitBreakerStatesVar.Get(context)),
                    BudgetStateJsonProp = new(context => budgetStateVar.Get(context))
                },
                successCheckIf
            }
        };

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

    /// <summary>
    /// Gets a role-based system prompt. In production, ResolveLlmPromptActivity
    /// handles the full 6-level hierarchy via IConfiguration DI. This provides
    /// a fallback for the inline Sequence pattern.
    /// </summary>
    private static string GetRolePrompt(string role)
    {
        return role.ToLowerInvariant() switch
        {
            "mentor" => "You are an experienced software development mentor guiding a junior developer. " +
                        "Provide encouraging, educational explanations. Use Socratic questioning when appropriate.",
            "analyst" => "You are a technical analyst specializing in software development. " +
                         "Analyze code, diagnose issues, and provide structured assessments. Be precise and evidence-based.",
            "implementer" => "You are an expert software developer. Write clean, well-tested, production-quality code. " +
                            "Follow established patterns and conventions.",
            "reviewer" => "You are an expert code reviewer. Identify bugs, security issues, performance problems, " +
                         "and style violations. Provide specific, actionable feedback.",
            _ => "You are Tamma, an AI-powered development assistant. Provide clear, accurate, and helpful responses. " +
                 "Focus on actionable guidance and best practices. Be concise but thorough."
        };
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
            return false;
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
            return false;
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
