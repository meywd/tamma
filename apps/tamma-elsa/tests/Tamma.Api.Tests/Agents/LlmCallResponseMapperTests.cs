using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NUnit.Framework;
using Tamma.Api.Services.Agents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 32-5 (T3) — <see cref="LlmCallResponseMapper"/> projection +
/// <c>ToHttpResult</c> §2.4 status discipline. The mapper NEVER returns a raw
/// 5xx (that would null the engine's <c>LastDiagnostic.HttpStatusCode</c> and
/// break <c>RetryCheck</c>/the breaker).
/// </summary>
[TestFixture]
public class LlmCallResponseMapperTests
{
    private LlmCallResponseMapper _mapper = null!;

    [SetUp]
    public void SetUp() => _mapper = new LlmCallResponseMapper();

    // -------------------------------------------------------------------
    // ToResponse — full projection
    // -------------------------------------------------------------------

    [Test]
    public void ToResponse_Success_ProjectsEveryField()
    {
        var agentId = Guid.NewGuid();
        var run = new AgentRunResult
        {
            AgentId = agentId,
            Version = 4,
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            Role = "developer",
            InputTokens = 120,
            OutputTokens = 80,
            CostUsd = 0.0042m,
            DurationMs = 999,
            Success = true,
            ToolCalls = new[] { new ToolCallDto { Name = "read_file", Id = "tc-1", ArgumentsJson = "{}" } },
            CorrelationId = "corr-1",
            CredentialSource = "platform",
            ResponseText = "all done",
        };

        var resp = _mapper.ToResponse(run);

        resp.Success.Should().BeTrue();
        resp.Text.Should().Be("all done");
        resp.ProviderUsed.Should().Be("anthropic");
        resp.ModelUsed.Should().Be("claude-sonnet-4");
        resp.Role.Should().Be("developer");
        resp.AgentId.Should().Be(agentId);
        resp.AgentVersion.Should().Be(4);
        resp.CorrelationId.Should().Be("corr-1");
        resp.DurationMs.Should().Be(999);
        resp.CredentialSource.Should().Be("platform");
        resp.Usage.PromptTokens.Should().Be(120);
        resp.Usage.CompletionTokens.Should().Be(80);
        resp.Usage.TotalTokens.Should().Be(200);
        resp.Cost.ProviderCostUsd.Should().Be(0.0042m);
        resp.Cost.Currency.Should().Be("USD");
        resp.ToolCalls.Should().ContainSingle().Which.Name.Should().Be("read_file");
        resp.FailureCode.Should().BeNull();
        resp.FailureReason.Should().BeNull();
        resp.HttpStatusCode.Should().BeNull();
    }

    [Test]
    public void ToResponse_Failure_CarriesCodeReasonStatusAndAccruedUsage()
    {
        var run = new AgentRunResult
        {
            Provider = "anthropic",
            Model = "claude-sonnet-4",
            Role = "developer",
            InputTokens = 10,
            OutputTokens = 0,
            Success = false,
            CorrelationId = "corr-2",
            CredentialSource = "byok",
            FailureCode = AgentRunFailureCodes.ProviderError,
            FailureReason = "upstream 429",
            HttpStatusCode = 429,
        };

        var resp = _mapper.ToResponse(run);

        resp.Success.Should().BeFalse();
        resp.FailureCode.Should().Be("PROVIDER_ERROR");
        resp.FailureReason.Should().Be("upstream 429");
        resp.HttpStatusCode.Should().Be(429);
        resp.CredentialSource.Should().Be("byok");
        resp.ProviderUsed.Should().Be("anthropic");
        resp.Usage.PromptTokens.Should().Be(10);
        resp.Text.Should().BeNull();
    }

    // -------------------------------------------------------------------
    // ToHttpResult — §2.4 status discipline
    // -------------------------------------------------------------------

    [Test]
    public void ToHttpResult_Success_Is200()
    {
        var run = SuccessRun();

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(200);
    }

    [Test]
    public void ToHttpResult_ProviderError_Is200SuccessFalse_WithStatusPreserved()
    {
        var run = FailRun(AgentRunFailureCodes.ProviderError, httpStatus: 429);

        var result = _mapper.ToHttpResult(run);

        // The HTTP envelope is 200 (so PostAsync<T> does not null the body) and
        // the preserved 429 rides inside the body for RetryCheck/the breaker.
        StatusOf(result).Should().Be(200);
        BodyOf(result).Success.Should().BeFalse();
        BodyOf(result).HttpStatusCode.Should().Be(429);
        BodyOf(result).FailureCode.Should().Be("PROVIDER_ERROR");
    }

    [Test]
    public void ToHttpResult_CredentialUnavailable_Is200SuccessFalse()
    {
        var run = FailRun(AgentRunFailureCodes.CredentialUnavailable, httpStatus: null);

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(200);
        BodyOf(result).Success.Should().BeFalse();
        BodyOf(result).FailureCode.Should().Be("PROVIDER_CREDENTIAL_UNAVAILABLE");
    }

    [Test]
    public void ToHttpResult_BudgetExceeded_Is200SuccessFalse()
    {
        var run = FailRun(AgentRunFailureCodes.BudgetExceeded, httpStatus: null);

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(200);
        BodyOf(result).Success.Should().BeFalse();
        BodyOf(result).FailureCode.Should().Be("BUDGET_EXCEEDED");
    }

    [Test]
    public void ToHttpResult_LoopExhausted_Is200SuccessFalse()
    {
        var run = FailRun(AgentRunFailureCodes.LoopExhausted, httpStatus: null);

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(200);
        BodyOf(result).Success.Should().BeFalse();
        BodyOf(result).FailureCode.Should().Be("LOOP_EXHAUSTED");
    }

    [Test]
    public void ToHttpResult_SaasProviderNotAllowed_Is400()
    {
        var run = FailRun(AgentRunFailureCodes.SaasProviderNotAllowed, httpStatus: null);

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(400);
    }

    [Test]
    public void ToHttpResult_TenantNotEntitled_Is403()
    {
        var run = FailRun(AgentRunFailureCodes.TenantNotEntitled, httpStatus: null);

        var result = _mapper.ToHttpResult(run);

        StatusOf(result).Should().Be(403);
    }

    [Test]
    public void ToHttpResult_NeverReturns5xx_ForAnyFailureCode()
    {
        var codes = new[]
        {
            AgentRunFailureCodes.ProviderError,
            AgentRunFailureCodes.CredentialUnavailable,
            AgentRunFailureCodes.BudgetExceeded,
            AgentRunFailureCodes.LoopExhausted,
            AgentRunFailureCodes.SaasProviderNotAllowed,
            AgentRunFailureCodes.TenantNotEntitled,
            "SOME_UNEXPECTED_CODE",
        };

        foreach (var code in codes)
        {
            var status = StatusOf(_mapper.ToHttpResult(FailRun(code, httpStatus: 503)));
            status.Should().BeLessThan(500, $"failure code '{code}' must never map to a 5xx");
        }
    }

    // -------------------------------------------------------------------
    // helpers
    // -------------------------------------------------------------------

    private static AgentRunResult SuccessRun() => new()
    {
        Provider = "anthropic",
        Model = "claude-sonnet-4",
        Role = "developer",
        Success = true,
        CorrelationId = "c",
        CredentialSource = "platform",
        ResponseText = "ok",
    };

    private static AgentRunResult FailRun(string code, int? httpStatus) => new()
    {
        Provider = "anthropic",
        Model = "claude-sonnet-4",
        Role = "developer",
        Success = false,
        CorrelationId = "c",
        FailureCode = code,
        FailureReason = "reason",
        HttpStatusCode = httpStatus,
    };

    /// <summary>Extract the status code from a minimal-API <see cref="IResult"/>
    /// (Ok/JsonHttpResult expose StatusCode via the typed result types).</summary>
    private static int StatusOf(IResult result)
    {
        // Json<T>/Ok<T>/StatusCode-bearing results expose a StatusCode property.
        var prop = result.GetType().GetProperty("StatusCode",
            BindingFlags.Public | BindingFlags.Instance);
        var raw = prop?.GetValue(result);
        return raw switch
        {
            int i => i,
            null => 200, // a bare Ok<T> reports null StatusCode but is a 200.
            _ => Convert.ToInt32(raw),
        };
    }

    /// <summary>Extract the <see cref="LlmCallResponse"/> body from a typed
    /// result (Ok&lt;T&gt; / JsonHttpResult&lt;T&gt; expose a Value).</summary>
    private static LlmCallResponse BodyOf(IResult result)
    {
        var prop = result.GetType().GetProperty("Value",
            BindingFlags.Public | BindingFlags.Instance);
        prop.Should().NotBeNull("the result should be a typed body-bearing result");
        return (LlmCallResponse)prop!.GetValue(result)!;
    }
}
