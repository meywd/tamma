using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Services;
using Tamma.Api.Tests.Infrastructure;

namespace Tamma.Api.Tests.Services;

/// <summary>
/// Review fix (CRITICAL) — <see cref="ElsaWorkflowService.StartWorkflowAsync"/>
/// must NEVER log the dispatch input's values. The rotate-secret dispatch puts
/// the operator's NEW secret plaintext under the <c>newPlaintext</c> key; the
/// previous code destructured the entire dict (<c>{@Input}</c>) at Information
/// level, leaking the plaintext in cleartext. The fix logs the workflow name +
/// the input KEY SET only (sensitive keys shown as <c>name=[redacted]</c>).
///
/// <para>The log line runs BEFORE the ELSA health-check / HTTP call, so we can
/// capture it then let the (no-network) dispatch fail — the assertion is on the
/// captured log content, not on the dispatch result.</para>
/// </summary>
[TestFixture]
public sealed class ElsaWorkflowServiceLoggingTests
{
    private const string Plaintext = "S3cr3t-Rotated-Value-9f3a2b71c0d4";

    private static (ElsaWorkflowService Svc, CapturingLoggerProvider Logs) Build()
    {
        var provider = new CapturingLoggerProvider();
        // Do NOT dispose the factory — that would dispose the provider and we
        // need it live to capture the synchronous log line below.
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(provider));
        var logger = loggerFactory.CreateLogger<ElsaWorkflowService>();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Point at a black hole so EnsureHealthyAsync fails fast AFTER
                // the log line we care about has already been emitted.
                ["Elsa:ServerUrl"] = "http://127.0.0.1:1",
            })
            .Build();

        var svc = new ElsaWorkflowService(new SimpleHttpClientFactory(), config, logger);
        return (svc, provider);
    }

    [Test]
    public void StartWorkflow_NeverLogsPlaintext_LogsRedactedKeySet()
    {
        var (svc, logs) = Build();

        var input = new Dictionary<string, object>
        {
            ["secretId"] = Guid.NewGuid().ToString(),
            ["rotationCorrelationId"] = "rot_abc",
            ["operatorUserId"] = Guid.NewGuid().ToString(),
            ["graceWindowSeconds"] = 900L,
            ["newPlaintext"] = Plaintext, // <- must never reach a log
        };

        // The "Starting workflow ... with input keys" log line is the FIRST
        // statement in StartWorkflowAsync and is emitted synchronously before
        // any await (the health check / HTTP call). Kick the call off and
        // observe the log without waiting out the (no-server) health-check
        // retry loop. The background task's eventual failure is irrelevant —
        // observe it so it doesn't surface as an unobserved task exception.
        var pending = svc.StartWorkflowAsync("rotate-secret", input);
        pending.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        var all = string.Join("\n", logs.Messages);

        // CRITICAL: the plaintext must NOT appear in ANY captured log message.
        all.Should().NotContain(Plaintext);
        // The sensitive key's PRESENCE is auditable, redacted.
        all.Should().Contain("newPlaintext=[redacted]");
        // Non-sensitive keys are listed by name (no values) so the dispatch is
        // still observable.
        all.Should().Contain("secretId");
        all.Should().Contain("rotationCorrelationId");
        // The workflow name is logged.
        all.Should().Contain("rotate-secret");
    }

    [Test]
    public void StartWorkflow_RedactsAnySensitiveKeyPattern()
    {
        var (svc, logs) = Build();

        var input = new Dictionary<string, object>
        {
            ["apiKey"] = "sk-should-not-appear",
            ["userPassword"] = "pw-should-not-appear",
            ["authToken"] = "tok-should-not-appear",
            ["dbSecret"] = "secret-should-not-appear",
            ["plainField"] = "ok-to-list-name-only",
        };

        var pending = svc.StartWorkflowAsync("some-workflow", input);
        pending.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        var all = string.Join("\n", logs.Messages);

        all.Should().NotContain("sk-should-not-appear");
        all.Should().NotContain("pw-should-not-appear");
        all.Should().NotContain("tok-should-not-appear");
        all.Should().NotContain("secret-should-not-appear");
        // Value of the plain field is also never logged (keys only), but its
        // name is.
        all.Should().NotContain("ok-to-list-name-only");
        all.Should().Contain("plainField");
        all.Should().Contain("apiKey=[redacted]");
    }

    /// <summary>
    /// Minimal <see cref="IHttpClientFactory"/> — returns a plain HttpClient.
    /// The service's health check targets an unreachable address so it fails
    /// fast after the (already-emitted) log line.
    /// </summary>
    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new() { Timeout = TimeSpan.FromMilliseconds(200) };
    }
}
