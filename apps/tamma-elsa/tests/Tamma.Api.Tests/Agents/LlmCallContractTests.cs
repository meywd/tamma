using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.LlmCall.Models;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-5 (T1) — wire-contract guards for <see cref="LlmCallRequest"/> /
/// <see cref="LlmCallResponse"/> (design §2.2/§2.3), the internal
/// <see cref="ManagedAgentRequest"/>/<see cref="AgentRunResult"/> records, and
/// the load-bearing KEY-SAFETY invariant: the response type (and its nested
/// DTOs) NEVER exposes a property that could carry a provider API key — only the
/// <c>credentialSource</c> label ("byok"/"platform"). These are data invariants
/// the T3/T4 mapper upholds; here we just prove the shape supports them. No
/// behaviour is exercised.
/// </summary>
[TestFixture]
public class LlmCallContractTests
{
    // ---------------------------------------------------------------
    // LlmCallRequest — required fields + field set (design §2.2)
    // ---------------------------------------------------------------

    [Test]
    public void LlmCallRequest_RequiresRolePromptCorrelationId()
    {
        // The `required` modifier is a compile-time guard; this construction
        // proves the three required members are present and settable. If any of
        // Role/Prompt/CorrelationId were renamed/removed this would not compile.
        var req = new LlmCallRequest
        {
            Role = "developer",
            Prompt = "do the thing",
            CorrelationId = "wf-instance-1",
        };

        req.Role.Should().Be("developer");
        req.Prompt.Should().Be("do the thing");
        req.CorrelationId.Should().Be("wf-instance-1");
    }

    [Test]
    public void LlmCallRequest_OptionalFields_DefaultSensibly()
    {
        var req = new LlmCallRequest
        {
            Role = "developer",
            Prompt = "p",
            CorrelationId = "c",
        };

        req.TenantId.Should().BeNull();
        req.AgentId.Should().BeNull();
        req.Persona.Should().BeNull();
        req.Action.Should().BeNull();
        req.Phase.Should().BeNull();
        req.Model.Should().BeNull();
        req.Tools.Should().BeNull();
        req.EnableToolLoop.Should().BeFalse();
        req.ToolLoopConfig.Should().BeNull();
        req.Variables.Should().NotBeNull().And.BeEmpty();
        req.Params.Should().NotBeNull();
    }

    [Test]
    public void LlmCallParams_DefaultsMatchDesign()
    {
        var p = new LlmCallParams();

        p.MaxTokens.Should().Be(4096);
        p.Temperature.Should().Be(0.7);
        p.BudgetCapUsd.Should().Be(0m);
    }

    [Test]
    public void LlmCallRequest_CarriesAllWireFields()
    {
        var tenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var loopCfg = new ToolLoopConfig { MaxSteps = 5 };

        var req = new LlmCallRequest
        {
            TenantId = tenantId,
            AgentId = agentId,
            Persona = "claude",
            Role = "code_reviewer",
            Action = "review",
            Phase = "review_phase",
            Prompt = "review this",
            Variables = new Dictionary<string, object?> { ["k"] = "v" },
            Model = "claude-sonnet-4",
            Tools = new[] { "read_file" },
            EnableToolLoop = true,
            ToolLoopConfig = loopCfg,
            Params = new LlmCallParams { MaxTokens = 8000, Temperature = 0.2, BudgetCapUsd = 1.5m },
            CorrelationId = "corr-1",
        };

        req.TenantId.Should().Be(tenantId);
        req.AgentId.Should().Be(agentId);
        req.Persona.Should().Be("claude");
        req.Role.Should().Be("code_reviewer");
        req.Action.Should().Be("review");
        req.Phase.Should().Be("review_phase");
        req.Prompt.Should().Be("review this");
        req.Variables.Should().ContainKey("k");
        req.Model.Should().Be("claude-sonnet-4");
        req.Tools.Should().ContainSingle().Which.Should().Be("read_file");
        req.EnableToolLoop.Should().BeTrue();
        req.ToolLoopConfig.Should().BeSameAs(loopCfg);
        req.Params.MaxTokens.Should().Be(8000);
        req.CorrelationId.Should().Be("corr-1");
    }

    // ---------------------------------------------------------------
    // ManagedAgentRequest.From — tenant derivation + carry-forward
    // ---------------------------------------------------------------

    [Test]
    public void ManagedAgentRequest_From_BodyTenant_CannotOverrideAuthoritativeTenant()
    {
        // Finding C1 — even when the body names a DIFFERENT tenant, the
        // auth-derived (authoritative) tenant wins. The body tenantId carries
        // no server-side authority, so a caller cannot be gated / budgeted /
        // credentialed as a tenant other than its authenticated scope.
        var bodyTenant = Guid.NewGuid();
        var authoritativeTenant = Guid.NewGuid();
        var req = new LlmCallRequest
        {
            TenantId = bodyTenant,
            Role = "developer",
            Prompt = "p",
            CorrelationId = "c",
        };

        var mapped = ManagedAgentRequest.From(req, authoritativeTenant);

        mapped.TenantId.Should().Be(authoritativeTenant,
            "the auth-derived tenant is authoritative; the body tenantId never overrides it (C1)");
        mapped.TenantId.Should().NotBe(bodyTenant);
    }

    [Test]
    public void ManagedAgentRequest_From_UsesAuthoritativeTenant_IgnoringBody()
    {
        var authoritativeTenant = Guid.NewGuid();
        var req = new LlmCallRequest
        {
            TenantId = null,
            Role = "developer",
            Prompt = "p",
            CorrelationId = "c",
        };

        var mapped = ManagedAgentRequest.From(req, authoritativeTenant);

        mapped.TenantId.Should().Be(authoritativeTenant, "the auth-derived tenant is the scope");
    }

    [Test]
    public void ManagedAgentRequest_From_NullAuthoritative_StaysNull_EvenIfBodySet()
    {
        // A platform/single-user request (no authenticated tenant) must NOT
        // pick up a tenant from the body — null authoritative ⇒ null scope.
        var req = new LlmCallRequest
        {
            TenantId = Guid.NewGuid(),
            Role = "developer",
            Prompt = "p",
            CorrelationId = "c",
        };

        var mapped = ManagedAgentRequest.From(req, null);

        mapped.TenantId.Should().BeNull("single-user / platform scope; body tenantId is ignored (C1)");
    }

    [Test]
    public void ManagedAgentRequest_From_CarriesAllFields()
    {
        var agentId = Guid.NewGuid();
        var loopCfg = new ToolLoopConfig { MaxSteps = 7 };
        var req = new LlmCallRequest
        {
            AgentId = agentId,
            Persona = "gemini",
            Role = "developer",
            Action = "implement",
            Phase = "impl",
            Prompt = "build it",
            Variables = new Dictionary<string, object?> { ["x"] = 1 },
            Model = "gemini-2.5-pro",
            Tools = new[] { "write_file" },
            EnableToolLoop = true,
            ToolLoopConfig = loopCfg,
            Params = new LlmCallParams { MaxTokens = 2048, Temperature = 0.1, BudgetCapUsd = 0.25m },
            CorrelationId = "corr-z",
        };

        var mapped = ManagedAgentRequest.From(req, Guid.NewGuid());

        mapped.AgentId.Should().Be(agentId);
        mapped.Persona.Should().Be("gemini");
        mapped.Role.Should().Be("developer");
        mapped.Action.Should().Be("implement");
        mapped.Phase.Should().Be("impl");
        mapped.Prompt.Should().Be("build it");
        mapped.Variables.Should().ContainKey("x");
        mapped.Model.Should().Be("gemini-2.5-pro");
        mapped.Tools.Should().ContainSingle().Which.Should().Be("write_file");
        mapped.EnableToolLoop.Should().BeTrue();
        mapped.ToolLoopConfig.Should().BeSameAs(loopCfg);
        mapped.Params.MaxTokens.Should().Be(2048);
        mapped.Params.Temperature.Should().Be(0.1);
        mapped.Params.BudgetCapUsd.Should().Be(0.25m);
        mapped.CorrelationId.Should().Be("corr-z");
    }

    // ---------------------------------------------------------------
    // LlmCallResponse — success vs failure shape (design §2.3 / AC7)
    // ---------------------------------------------------------------

    [Test]
    public void LlmCallResponse_Success_LeavesFailureFieldsNull()
    {
        var resp = new LlmCallResponse
        {
            Success = true,
            Text = "done",
            CorrelationId = "c",
            CredentialSource = "platform",
            Usage = new UsageDto { PromptTokens = 10, CompletionTokens = 20, TotalTokens = 30 },
            Cost = new CostDto { ProviderCostUsd = 0.001m, PriceUsd = 0.002m },
        };

        resp.Success.Should().BeTrue();
        resp.FailureCode.Should().BeNull();
        resp.FailureReason.Should().BeNull();
        resp.HttpStatusCode.Should().BeNull();
        resp.Text.Should().Be("done");
        resp.Cost.Currency.Should().Be("USD");
        resp.ToolCalls.Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public void LlmCallResponse_Failure_CarriesCodeReasonAndStatus()
    {
        // AC7: an expected execution failure preserves httpStatusCode so the
        // engine's RetryCheck + circuit breaker keep working. The shape MUST
        // support carrying all three on a Success==false instance.
        var resp = new LlmCallResponse
        {
            Success = false,
            CorrelationId = "c",
            CredentialSource = "byok",
            ProviderUsed = "anthropic",
            FailureCode = "PROVIDER_ERROR",
            FailureReason = "upstream 503",
            HttpStatusCode = 503,
            Usage = new UsageDto { PromptTokens = 5, CompletionTokens = 0, TotalTokens = 5 },
        };

        resp.Success.Should().BeFalse();
        resp.FailureCode.Should().Be("PROVIDER_ERROR");
        resp.FailureReason.Should().Be("upstream 503");
        resp.HttpStatusCode.Should().Be(503);
        resp.Usage.PromptTokens.Should().Be(5);
    }

    // ---------------------------------------------------------------
    // credentialSource invariant: only "byok" | "platform" | null
    // ---------------------------------------------------------------

    [Test]
    public void CredentialSourceLabels_AreByokOrPlatform()
    {
        // The two enum values lowercase to exactly the two valid wire labels.
        var byok = CredentialSourceLabel.From(CredentialSource.Byok);
        var platform = CredentialSourceLabel.From(CredentialSource.Platform);

        byok.Should().Be("byok");
        platform.Should().Be("platform");

        CredentialSourceLabel.Byok.Should().Be("byok");
        CredentialSourceLabel.Platform.Should().Be("platform");

        var resp = new LlmCallResponse { Success = true, CorrelationId = "c", CredentialSource = byok };
        new[] { CredentialSourceLabel.Byok, CredentialSourceLabel.Platform, null }
            .Should().Contain(resp.CredentialSource);
    }

    // ---------------------------------------------------------------
    // KEY-SAFETY reflection invariant (load-bearing)
    // ---------------------------------------------------------------

    [Test]
    public void LlmCallResponse_AndNestedDtos_ExposeNoKeyBearingProperty()
    {
        // Allowed exceptions:
        //  - "CredentialSource" — the byok/platform label (not the key).
        //  - token COUNT fields ending in "Tokens" — they carry an int count.
        var types = new[]
        {
            typeof(LlmCallResponse),
            typeof(UsageDto),
            typeof(CostDto),
            typeof(ToolCallDto),
        };

        var banned = new[] { "Key", "ApiKey", "Secret", "Credential", "Token" };
        var tokenCountAllow = new[]
        {
            "PromptTokens", "CompletionTokens", "TotalTokens", "ToolLoopTokens",
        };

        foreach (var type in types)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var name = prop.Name;

                if (name == "CredentialSource")
                {
                    continue; // the label, never the key
                }

                if (tokenCountAllow.Contains(name) && prop.PropertyType == typeof(int))
                {
                    continue; // token COUNT (int), not a token string
                }

                foreach (var bad in banned)
                {
                    name.Contains(bad, StringComparison.OrdinalIgnoreCase)
                        .Should().BeFalse(
                            $"{type.Name}.{name} must not look like it carries a key/secret/token " +
                            $"(matched banned fragment '{bad}')");
                }
            }
        }
    }

    [Test]
    public void AgentRunResult_HoldsTheProducerShape()
    {
        // T1 only defines the type; T3 produces it. Prove the structured-outcome
        // shape (AC10) is constructible and key-free in the same way.
        var agentId = Guid.NewGuid();
        var result = new AgentRunResult
        {
            AgentId = agentId,
            Version = 3,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            Role = "developer",
            InputTokens = 100,
            OutputTokens = 50,
            CostUsd = 0.0123m,
            DurationMs = 1234,
            Success = true,
            ToolCalls = new[] { new ToolCallDto { Name = "read_file", Id = "tc-1", ArgumentsJson = "{}" } },
            CorrelationId = "corr",
            CredentialSource = "platform",
            ResponseText = "ok",
        };

        result.AgentId.Should().Be(agentId);
        result.Version.Should().Be(3);
        result.Success.Should().BeTrue();
        result.FailureCode.Should().BeNull();
        result.FailureReason.Should().BeNull();
        result.HttpStatusCode.Should().BeNull();
        result.ToolCalls.Should().ContainSingle();

        // Same key-safety guard on the producer record.
        var banned = new[] { "Key", "ApiKey", "Secret", "Token" };
        foreach (var prop in typeof(AgentRunResult).GetProperties())
        {
            if (prop.Name == "CredentialSource")
            {
                continue;
            }

            // Token COUNT fields (int) are not secret-bearing — same carve-out
            // as UsageDto.ToolLoopTokens in the response-shape guard above.
            if (prop.Name is "InputTokens" or "OutputTokens" or "ToolLoopTokens"
                && prop.PropertyType == typeof(int))
            {
                continue;
            }

            foreach (var bad in banned)
            {
                prop.Name.Contains(bad, StringComparison.OrdinalIgnoreCase)
                    .Should().BeFalse($"AgentRunResult.{prop.Name} matched banned fragment '{bad}'");
            }
        }
    }

    [Test]
    public void AgentRunEventTypes_FollowAggregateActionStatusPattern()
    {
        AgentRunEventTypes.Started.Should().Be("AGENT.RUN.STARTED");
        AgentRunEventTypes.Success.Should().Be("AGENT.RUN.SUCCESS");
        AgentRunEventTypes.Failed.Should().Be("AGENT.RUN.FAILED");
    }

    // ---------------------------------------------------------------
    // Finding I-2 — pin the dual-DTO wire contract (engine ⇆ API)
    // ---------------------------------------------------------------
    //
    // There are TWO record sets on the wire: the engine's Tamma.Activities
    // LlmCallApiRequest/Response (with [JsonPropertyName] camelCase) and the API's
    // Tamma.Api LlmCallRequest/Response (serialized under JsonNamingPolicy.CamelCase).
    // They must stay byte-compatible. These round-trip tests serialize one side
    // exactly as it goes over the wire and deserialize into the OTHER, asserting
    // every field survives — catching future drift between the two sets.

    /// <summary>The API host's serializer policy (Program.cs ConfigureHttpJsonOptions):
    /// CamelCase naming + case-insensitive.</summary>
    private static readonly JsonSerializerOptions ApiOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The engine client's serializer policy (TammaApiClient.JsonOpts):
    /// case-insensitive; the [JsonPropertyName] attributes drive the camelCase
    /// names on the engine DTOs.</summary>
    private static readonly JsonSerializerOptions EngineOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Test]
    public void WireContract_EngineRequest_DeserializesInto_ApiRequest_EveryFieldSurvives()
    {
        var tenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var engineReq = new LlmCallApiRequest
        {
            TenantId = tenantId,
            AgentId = agentId,
            Persona = "claude",
            Provider = "openrouter",
            Role = "code_reviewer",
            Action = "review",
            Phase = "review_phase",
            Prompt = "review this",
            Variables = new Dictionary<string, object?> { ["k"] = "v" },
            Model = "anthropic/claude-sonnet-4",
            Tools = new[] { "read_file", "write_file" },
            EnableToolLoop = true,
            ToolLoopConfig = new ToolLoopConfig { MaxSteps = 9 },
            Params = new LlmCallApiParams { MaxTokens = 8000, Temperature = 0.2, BudgetCapUsd = 1.5m },
            CorrelationId = "corr-1",
        };

        // Engine serializes (its [JsonPropertyName] camelCase) → API deserializes
        // (CamelCase policy). Every field must survive the boundary.
        var json = JsonSerializer.Serialize(engineReq, EngineOpts);
        var apiReq = JsonSerializer.Deserialize<LlmCallRequest>(json, ApiOpts);

        apiReq.Should().NotBeNull();
        apiReq!.TenantId.Should().Be(tenantId);
        apiReq.AgentId.Should().Be(agentId);
        apiReq.Persona.Should().Be("claude");
        apiReq.Provider.Should().Be("openrouter", "Finding I-1's provider override must survive the wire");
        apiReq.Role.Should().Be("code_reviewer");
        apiReq.Action.Should().Be("review");
        apiReq.Phase.Should().Be("review_phase");
        apiReq.Prompt.Should().Be("review this");
        apiReq.Variables.Should().ContainKey("k");
        apiReq.Model.Should().Be("anthropic/claude-sonnet-4");
        apiReq.Tools.Should().Equal("read_file", "write_file");
        apiReq.EnableToolLoop.Should().BeTrue();
        apiReq.ToolLoopConfig!.MaxSteps.Should().Be(9);
        apiReq.Params.MaxTokens.Should().Be(8000);
        apiReq.Params.Temperature.Should().Be(0.2);
        apiReq.Params.BudgetCapUsd.Should().Be(1.5m);
        apiReq.CorrelationId.Should().Be("corr-1");
    }

    [Test]
    public void WireContract_ApiRequest_DeserializesInto_EngineRequest_EveryFieldSurvives()
    {
        var tenantId = Guid.NewGuid();
        var agentId = Guid.NewGuid();
        var apiReq = new LlmCallRequest
        {
            TenantId = tenantId,
            AgentId = agentId,
            Persona = "gemini",
            Provider = "openai",
            Role = "developer",
            Action = "implement",
            Phase = "impl",
            Prompt = "build it",
            Variables = new Dictionary<string, object?> { ["x"] = "1" },
            Model = "gpt-4o",
            Tools = new[] { "write_file" },
            EnableToolLoop = true,
            ToolLoopConfig = new ToolLoopConfig { MaxSteps = 4 },
            Params = new LlmCallParams { MaxTokens = 2048, Temperature = 0.1, BudgetCapUsd = 0.25m },
            CorrelationId = "corr-z",
        };

        // API serializes (CamelCase policy) → engine deserializes (its [JsonPropertyName]).
        var json = JsonSerializer.Serialize(apiReq, ApiOpts);
        var engineReq = JsonSerializer.Deserialize<LlmCallApiRequest>(json, EngineOpts);

        engineReq.Should().NotBeNull();
        engineReq!.TenantId.Should().Be(tenantId);
        engineReq.AgentId.Should().Be(agentId);
        engineReq.Persona.Should().Be("gemini");
        engineReq.Provider.Should().Be("openai");
        engineReq.Role.Should().Be("developer");
        engineReq.Action.Should().Be("implement");
        engineReq.Phase.Should().Be("impl");
        engineReq.Prompt.Should().Be("build it");
        engineReq.Variables.Should().ContainKey("x");
        engineReq.Model.Should().Be("gpt-4o");
        engineReq.Tools.Should().Equal("write_file");
        engineReq.EnableToolLoop.Should().BeTrue();
        engineReq.ToolLoopConfig!.MaxSteps.Should().Be(4);
        engineReq.Params.MaxTokens.Should().Be(2048);
        engineReq.Params.Temperature.Should().Be(0.1);
        engineReq.Params.BudgetCapUsd.Should().Be(0.25m);
        engineReq.CorrelationId.Should().Be("corr-z");
    }

    [Test]
    public void WireContract_ApiResponse_DeserializesInto_EngineResponse_EveryFieldSurvives()
    {
        var agentId = Guid.NewGuid();
        var apiResp = new LlmCallResponse
        {
            Success = false,
            Text = "partial",
            Usage = new UsageDto
            {
                PromptTokens = 120, CompletionTokens = 60, TotalTokens = 180,
                ToolLoopTokens = 180, ToolLoopTurns = 3, ToolLoopExhausted = true,
            },
            CredentialSource = "platform",
            ProviderUsed = "anthropic",
            ModelUsed = "claude-sonnet-4",
            Cost = new CostDto { ProviderCostUsd = 0.003m, PriceUsd = 0.004m, Currency = "USD" },
            ToolCalls = new[] { new ToolCallDto { Name = "read_file", Id = "tc-1", ArgumentsJson = "{\"p\":\"a\"}" } },
            AgentId = agentId,
            AgentVersion = 5,
            Role = "developer",
            CorrelationId = "corr-r",
            DurationMs = 1234,
            FailureCode = "PROVIDER_ERROR",
            FailureReason = "upstream 503",
            HttpStatusCode = 503,
        };

        // API serializes (CamelCase) → engine deserializes (its [JsonPropertyName]).
        var json = JsonSerializer.Serialize(apiResp, ApiOpts);
        var engineResp = JsonSerializer.Deserialize<LlmCallApiResponse>(json, EngineOpts);

        engineResp.Should().NotBeNull();
        engineResp!.Success.Should().BeFalse();
        engineResp.Text.Should().Be("partial");
        engineResp.Usage.PromptTokens.Should().Be(120);
        engineResp.Usage.CompletionTokens.Should().Be(60);
        engineResp.Usage.TotalTokens.Should().Be(180);
        engineResp.Usage.ToolLoopTokens.Should().Be(180);
        engineResp.Usage.ToolLoopTurns.Should().Be(3);
        engineResp.Usage.ToolLoopExhausted.Should().BeTrue();
        engineResp.CredentialSource.Should().Be("platform");
        engineResp.ProviderUsed.Should().Be("anthropic");
        engineResp.ModelUsed.Should().Be("claude-sonnet-4");
        engineResp.Cost.ProviderCostUsd.Should().Be(0.003m);
        engineResp.Cost.PriceUsd.Should().Be(0.004m);
        engineResp.Cost.Currency.Should().Be("USD");
        engineResp.ToolCalls.Should().ContainSingle();
        engineResp.ToolCalls[0].Name.Should().Be("read_file");
        engineResp.ToolCalls[0].Id.Should().Be("tc-1");
        engineResp.ToolCalls[0].ArgumentsJson.Should().Be("{\"p\":\"a\"}");
        engineResp.AgentId.Should().Be(agentId);
        engineResp.AgentVersion.Should().Be(5);
        engineResp.Role.Should().Be("developer");
        engineResp.CorrelationId.Should().Be("corr-r");
        engineResp.DurationMs.Should().Be(1234);
        engineResp.FailureCode.Should().Be("PROVIDER_ERROR");
        engineResp.FailureReason.Should().Be("upstream 503");
        engineResp.HttpStatusCode.Should().Be(503);
    }

    [Test]
    public void WireContract_EngineResponse_DeserializesInto_ApiResponse_EveryFieldSurvives()
    {
        var agentId = Guid.NewGuid();
        var engineResp = new LlmCallApiResponse
        {
            Success = true,
            Text = "done",
            Usage = new LlmCallUsageDto
            {
                PromptTokens = 10, CompletionTokens = 5, TotalTokens = 15,
                ToolLoopTokens = 15, ToolLoopTurns = 1, ToolLoopExhausted = false,
            },
            CredentialSource = "byok",
            ProviderUsed = "openai",
            ModelUsed = "gpt-4o",
            Cost = new LlmCallCostDto { ProviderCostUsd = 0.001m, PriceUsd = 0m, Currency = "USD" },
            ToolCalls = new[] { new LlmCallToolCallDto { Name = "write_file", Id = "tc-9", ArgumentsJson = "{}" } },
            AgentId = agentId,
            AgentVersion = 2,
            Role = "code_reviewer",
            CorrelationId = "corr-e",
            DurationMs = 99,
        };

        // Engine serializes ([JsonPropertyName] camelCase) → API deserializes (CamelCase policy).
        var json = JsonSerializer.Serialize(engineResp, EngineOpts);
        var apiResp = JsonSerializer.Deserialize<LlmCallResponse>(json, ApiOpts);

        apiResp.Should().NotBeNull();
        apiResp!.Success.Should().BeTrue();
        apiResp.Text.Should().Be("done");
        apiResp.Usage.PromptTokens.Should().Be(10);
        apiResp.Usage.CompletionTokens.Should().Be(5);
        apiResp.Usage.TotalTokens.Should().Be(15);
        apiResp.Usage.ToolLoopTokens.Should().Be(15);
        apiResp.Usage.ToolLoopTurns.Should().Be(1);
        apiResp.Usage.ToolLoopExhausted.Should().BeFalse();
        apiResp.CredentialSource.Should().Be("byok");
        apiResp.ProviderUsed.Should().Be("openai");
        apiResp.ModelUsed.Should().Be("gpt-4o");
        apiResp.Cost.ProviderCostUsd.Should().Be(0.001m);
        apiResp.Cost.PriceUsd.Should().Be(0m);
        apiResp.Cost.Currency.Should().Be("USD");
        apiResp.ToolCalls.Should().ContainSingle();
        apiResp.ToolCalls[0].Name.Should().Be("write_file");
        apiResp.ToolCalls[0].Id.Should().Be("tc-9");
        apiResp.AgentId.Should().Be(agentId);
        apiResp.AgentVersion.Should().Be(2);
        apiResp.Role.Should().Be("code_reviewer");
        apiResp.CorrelationId.Should().Be("corr-e");
        apiResp.DurationMs.Should().Be(99);
    }
}
