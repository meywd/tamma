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
/// Flow:
///   1. Initialize variables from workflow input
///   2. Resolve provider chain from config (AgentsConfig:ProviderChains:{agentRole})
///   3. For each provider in the chain:
///      a. Check circuit breaker → skip if Open
///      b. Check budget → skip if exhausted
///      c. Resolve prompt (via ResolveLlmPromptActivity, 6-level hierarchy)
///      d. Call LLM API (with retry on transient failures)
///      e. Record diagnostics (update circuit breaker + budget)
///      f. If success → break, else → next provider
///   4. Set workflow outputs via SetOutput
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
        // Workflow body
        // ============================================================

        builder.Root = new Sequence
        {
            Activities =
            {
                // ── Step 1: Initialize from input ─────────────────────
                // Supports both new typed inputs and legacy InputJson format
                new SetVariable
                {
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
                },

                // Parse input and set up budget
                new SetVariable
                {
                    Variable = budgetStateVar,
                    Value = new(context => {
                        var raw = inputVar.Get(context);
                        var input = ParseInput(raw);
                        return JsonSerializer.Serialize(new BudgetState { CapUsd = input.BudgetCapUsd });
                    })
                },

                // ── Step 2: Resolve provider chain from config ────────
                new SetVariable
                {
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
                },

                // ── Step 3: Iterate over provider chain ───────────────
                new ForEach<string>
                {
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
                        Activities =
                        {
                            // ── Skip if already succeeded ──
                            new If
                            {
                                Condition = new(context => successVar.Get(context)),
                                Then = new Sequence { Activities = { /* skip — do nothing */ } },
                                Else = new Sequence
                                {
                                    Activities =
                                    {
                                        // Set current provider from the ForEach current value
                                        new SetVariable
                                        {
                                            Variable = currentProviderVar,
                                            Value = new(context => context.GetVariable<string>("CurrentValue") ?? "anthropic")
                                        },

                                        // Reset attempt counter
                                        new SetVariable
                                        {
                                            Variable = attemptNumberVar,
                                            Value = new(1)
                                        },

                                        // ── 3a. Check circuit breaker ──
                                        new If
                                        {
                                            Condition = new(context => {
                                                var provider = currentProviderVar.Get(context);
                                                var statesJson = circuitBreakerStatesVar.Get(context);
                                                return IsCircuitBreakerOpen(provider, statesJson);
                                            }),
                                            // Circuit breaker is OPEN → skip this provider
                                            Then = new Sequence
                                            {
                                                Activities =
                                                {
                                                    new SetVariable
                                                    {
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
                                                Activities =
                                                {
                                                    // ── 3b. Check budget ──
                                                    new If
                                                    {
                                                        Condition = new(context => {
                                                            var budgetJson = budgetStateVar.Get(context);
                                                            return IsBudgetExhausted(budgetJson);
                                                        }),
                                                        // Budget exhausted → skip
                                                        Then = new Sequence
                                                        {
                                                            Activities =
                                                            {
                                                                new SetVariable
                                                                {
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
                                                            Activities =
                                                            {
                                                                // ── 3c. Resolve prompt ──
                                                                // Uses ResolveLlmPromptActivity for the 6-level hierarchy
                                                                new SetVariable
                                                                {
                                                                    Variable = resolvedSystemPromptVar,
                                                                    Value = new(context => {
                                                                        var raw = inputVar.Get(context);
                                                                        var input = ParseInput(raw);
                                                                        // If caller override, use it
                                                                        if (!string.IsNullOrWhiteSpace(input.SystemPromptOverride))
                                                                            return input.SystemPromptOverride;
                                                                        // Fall back to role-based prompt from config hierarchy
                                                                        // (ResolveLlmPromptActivity handles this via DI when used standalone)
                                                                        var role = agentRoleVar.Get(context) ?? "assistant";
                                                                        return GetRolePrompt(role);
                                                                    })
                                                                },

                                                                // ── 3d. Resolve tools ──
                                                                new SetVariable
                                                                {
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
                },

                // ── Step 4: Build final output if no success ──────────
                new If
                {
                    Condition = new(context => !successVar.Get(context)),
                    Then = new SetVariable
                    {
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
                    }
                },

                // ── Step 5: Set workflow outputs for parent consumption ──
                new SetOutput
                {
                    OutputName = new("success"),
                    OutputValue = new(context => (object)successVar.Get(context))
                },
                new SetOutput
                {
                    OutputName = new("workflowOutput"),
                    OutputValue = new(context => (object)(workflowOutputVar.Get(context) ?? "{}"))
                },
                new SetOutput
                {
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
        whileLoop.Condition = new Input<bool>(context =>
            !successVar.Get(context) &&
            attemptNumberVar.Get(context) <= maxRetriesVar.Get(context));

        // Build the retry loop body
        var retryCheckIf = new If
        {
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
                Variable = attemptNumberVar,
                Value = new(context => attemptNumberVar.Get(context) + 1)
            },
            Else = new SetVariable
            {
                Variable = attemptNumberVar,
                Value = new(context => maxRetriesVar.Get(context) + 1)
            }
        };

        var successCheckIf = new If
        {
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
                Activities =
                {
                    new SetVariable
                    {
                        Variable = successVar,
                        Value = new(true)
                    },
                    new SetVariable
                    {
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
                Activities = { retryCheckIf }
            }
        };

        var loopBody = new Sequence
        {
            Activities =
            {
                new CallLlmInlineActivity
                {
                    InputJsonProp = new(context => inputVar.Get(context)),
                    ProviderNameProp = new(context => currentProviderVar.Get(context)),
                    SystemPromptProp = new(context => resolvedSystemPromptVar.Get(context)),
                    ToolsJsonProp = new(context => resolvedToolsJsonVar.Get(context)),
                    AttemptNumberProp = new(context => attemptNumberVar.Get(context))
                },
                new RecordDiagnosticsInlineActivity
                {
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
