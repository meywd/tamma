using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using NUnit.Framework;

namespace Tamma.Activities.Guardrails.Tests;

/// <summary>
/// Story 38-4 (AC11) — analyzer unit tests: positive controls (direct vendor HTTP + vendor
/// injection + denied Slack send), negative controls (TammaApiClient / Engine:CallbackUrl /
/// variable-host / loopback), design-§5.3 exemptions, the non-engine-surface skip, and the
/// descriptor severity/id/category.
/// </summary>
[TestFixture]
public class EngineExternalCallAnalyzerTests
{
    // Minimal stubs for the vendor types the fixtures reference (declared with the exact
    // FQNs the analyzer matches, so the fixtures compile with no external references and the
    // analyzer's semantic-model FQN match is what is exercised).
    private const string Vendors = @"
namespace Octokit { public interface IGitHubClient { } public class GitHubClient { } }
namespace Tamma.Activities.AgentDispatch { public interface IGitHubActionsClient { } }
namespace Tamma.Activities.LlmCall.Credentials { public interface IProviderCredentialResolver { } }
namespace Tamma.Activities.LlmCall { public interface IInlineToolLoopRunner { } }
namespace SlackNet { public interface ISlackApiClient { } }
namespace Microsoft.Extensions.Configuration { public interface IConfiguration { } }
namespace Tamma.Core.Interfaces {
  public interface IIntegrationService {
    System.Threading.Tasks.Task SendSlackMessageAsync(string channel, string message);
    System.Threading.Tasks.Task SendSlackDirectMessageAsync(string userId, string message);
    System.Threading.Tasks.Task<bool> MergePullRequestAsync(string repo, int pr);
  }
}
// Minimal service-locator surface (DI container / Elsa's ActivityExecutionContext) — the
// generic <T> + typeof(T) resolve overloads the FIX I1 pass keys on.
public class ServiceCtx {
  public T GetService<T>() => throw new System.NotImplementedException();
  public T GetRequiredService<T>() => throw new System.NotImplementedException();
  public T GetKeyedService<T>(object key) => throw new System.NotImplementedException();
  public T GetRequiredKeyedService<T>(object key) => throw new System.NotImplementedException();
  public object GetService(System.Type serviceType) => throw new System.NotImplementedException();
}
";

    // ---------- (1) direct external HTTP → TAMMA001 --------------------------------------

    [Test]
    public Task DirectVendorHttp_PostAsJsonAsync_ToGitHub_Flags() => Verify.Engine(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class BadHttpActivity {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() {
        await {|TAMMA001:_http.PostAsJsonAsync(""https://api.github.com/repos/o/r/merges"", new { })|};
    }
}" + Vendors);

    [Test]
    public Task DirectVendorHttp_GetAsync_ToOpenAi_Flags() => Verify.Engine(@"
using System.Net.Http;
using System.Threading.Tasks;
public class BadHttpActivity {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() {
        await {|TAMMA001:_http.GetAsync(""https://api.openai.com/v1/models"")|};
    }
}" + Vendors);

    [Test]
    public Task DirectVendorHttp_InterpolatedLiteralHost_Flags() => Verify.Engine(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class BadHttpActivity {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run(string owner) {
        await {|TAMMA001:_http.PostAsJsonAsync($""https://api.github.com/repos/{owner}/x/merges"", new { })|};
    }
}" + Vendors);

    [Test]
    public Task DirectVendorHttp_OnElsaServerSurface_Flags() => Verify.ElsaServer(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class BadSeeder {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() {
        await {|TAMMA001:_http.PostAsJsonAsync(""https://slack.com/api/chat.postMessage"", new { })|};
    }
}" + Vendors);

    // ---------- (2) vendor-credential injection → TAMMA001 ------------------------------

    [Test]
    public Task OctokitClient_CtorInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    public BadActivity(Octokit.IGitHubClient {|TAMMA001:client|}) { }
}" + Vendors);

    [Test]
    public Task GitHubActionsClient_CtorInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    public BadActivity(Tamma.Activities.AgentDispatch.IGitHubActionsClient {|TAMMA001:actions|}) { }
}" + Vendors);

    [Test]
    public Task ProviderCredentialResolver_FieldInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    private Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver? {|TAMMA001:_resolver|};
}" + Vendors);

    [Test]
    public Task ProviderCredentialResolver_PropertyInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    public Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver? {|TAMMA001:Resolver|} { get; set; }
}" + Vendors);

    [Test]
    public Task SlackClient_CtorInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    public BadActivity(SlackNet.ISlackApiClient {|TAMMA001:slack|}) { }
}" + Vendors);

    // ---------- (3) denied Slack SEND invocation → TAMMA001 -----------------------------
    // Injecting the COMPOSITE IIntegrationService is allowed (no injection diagnostic); only
    // its Slack send methods are denied at the call site (Correction 2).

    [Test]
    public Task SendSlackMessageAsync_Invocation_Flags() => Verify.Engine(@"
using System.Threading.Tasks;
using Tamma.Core.Interfaces;
public class Notifier {
    private readonly IIntegrationService _svc;
    public Notifier(IIntegrationService svc) { _svc = svc; }
    public async Task Ping() {
        await {|TAMMA001:_svc.SendSlackMessageAsync(""chan"", ""hi"")|};
    }
}" + Vendors);

    [Test]
    public Task SendSlackDirectMessageAsync_Invocation_Flags() => Verify.Engine(@"
using System.Threading.Tasks;
using Tamma.Core.Interfaces;
public class Notifier {
    private readonly IIntegrationService _svc;
    public Notifier(IIntegrationService svc) { _svc = svc; }
    public async Task Ping() {
        await {|TAMMA001:_svc.SendSlackDirectMessageAsync(""U1"", ""hi"")|};
    }
}" + Vendors);

    [Test]
    public Task CompositeIntegrationService_NonSlackMethod_DoesNotFlag() => Verify.Engine(@"
using System.Threading.Tasks;
using Tamma.Core.Interfaces;
public class Merger {
    private readonly IIntegrationService _svc;
    public Merger(IIntegrationService svc) { _svc = svc; }
    public async Task Do() {
        await _svc.MergePullRequestAsync(""o/r"", 5);
    }
}" + Vendors);

    // ---------- (M1) injecting the Tamma.Api LLM core into an engine step → TAMMA001 -----
    // The whole-type EXEMPTION suppresses ONLY the runner's own members; INJECTING it into a
    // different engine activity would drive a credentialed LLM call from a workflow STEP.

    [Test]
    public Task InlineToolLoopRunner_CtorInjection_Flags() => Verify.Engine(@"
public class BadActivity {
    public BadActivity(Tamma.Activities.LlmCall.IInlineToolLoopRunner {|TAMMA001:runner|}) { }
}" + Vendors);

    // ---------- (I1) service-locator resolve of a denylisted vendor type → TAMMA001 ------

    [Test]
    public Task ServiceLocator_GetRequiredService_Octokit_Flags() => Verify.Engine(@"
public class BadActivity {
    private readonly ServiceCtx _ctx;
    public BadActivity(ServiceCtx ctx) { _ctx = ctx; }
    public void Run() {
        {|TAMMA001:_ctx.GetRequiredService<Octokit.IGitHubClient>()|};
    }
}" + Vendors);

    [Test]
    public Task ServiceLocator_GetService_CredentialResolver_Flags() => Verify.Engine(@"
public class BadActivity {
    private readonly ServiceCtx _ctx;
    public BadActivity(ServiceCtx ctx) { _ctx = ctx; }
    public void Run() {
        {|TAMMA001:_ctx.GetService<Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver>()|};
    }
}" + Vendors);

    [Test]
    public Task ServiceLocator_TypeofOverload_Octokit_Flags() => Verify.Engine(@"
public class BadActivity {
    private readonly ServiceCtx _ctx;
    public BadActivity(ServiceCtx ctx) { _ctx = ctx; }
    public void Run() {
        {|TAMMA001:_ctx.GetService(typeof(Octokit.IGitHubClient))|};
    }
}" + Vendors);

    [Test]
    public Task ServiceLocator_NonDenylistedTypes_DoesNotFlag() => Verify.Engine(@"
namespace Tamma.Activities.LlmCall { public class TammaApiClient { } }
public class GoodActivity {
    private readonly ServiceCtx _ctx;
    public GoodActivity(ServiceCtx ctx) { _ctx = ctx; }
    public void Run() {
        _ctx.GetService<Tamma.Activities.LlmCall.TammaApiClient>();
        _ctx.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        _ctx.GetService(typeof(Microsoft.Extensions.Configuration.IConfiguration));
    }
}" + Vendors);

    // ---------- (M2) `new <denylisted-vendor>()` construction → TAMMA001 -----------------
    // Stored in an `object` field so the field pass (2) does NOT fire — only the
    // ObjectCreation pass does, proving construction is inspected on its own.

    [Test]
    public Task VendorConstruction_NewOctokitClient_Flags() => Verify.Engine(@"
public class BadActivity {
    private readonly object _client = {|TAMMA001:new Octokit.GitHubClient()|};
}" + Vendors);

    [Test]
    public Task VendorConstruction_NonVendor_DoesNotFlag() => Verify.Engine(@"
public class GoodActivity {
    private readonly object _x = new object();
    private readonly System.Net.Http.HttpClient _http = new System.Net.Http.HttpClient();
}" + Vendors);

    // ---------- (M3) synchronous HttpClient.Send to a literal external host → TAMMA001 ---

    [Test]
    public Task DirectVendorHttp_SyncSend_LiteralHost_Flags() => Verify.Engine(@"
using System.Net.Http;
public class BadHttpActivity {
    private readonly HttpClient _http = new HttpClient();
    public void Run() {
        var resp = {|TAMMA001:_http.Send(new HttpRequestMessage(HttpMethod.Get, ""https://api.github.com/x""))|};
    }
}" + Vendors);

    // ---------- negative controls: sanctioned seams (AC4) → no diagnostic --------------

    [Test]
    public Task ThinClient_TammaApiClientCall_DoesNotFlag() => Verify.Engine(@"
using System.Threading.Tasks;
namespace Tamma.Activities.LlmCall {
  public class TammaApiClient {
    public Task<bool> QueueSlackNotificationAsync(object request) => Task.FromResult(true);
  }
}
public class ThinActivity {
    private readonly Tamma.Activities.LlmCall.TammaApiClient _api;
    public ThinActivity(Tamma.Activities.LlmCall.TammaApiClient api) { _api = api; }
    public async Task Run() { await _api.QueueSlackNotificationAsync(new { }); }
}" + Vendors);

    [Test]
    public Task EngineCallbackHost_InterpolatedPost_DoesNotFlag() => Verify.Engine(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class CiActivity {
    private readonly HttpClient _http = new HttpClient();
    private readonly string _callbackUrl = ""http://elsa-callback/root"";
    public async Task Run() {
        await _http.PostAsJsonAsync($""{_callbackUrl}/api/engine/trigger-ci"", new { });
    }
}" + Vendors);

    [Test]
    public Task VariableHostHttp_DoesNotFlag() => Verify.Engine(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class DynActivity {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run(string url) { await _http.PostAsJsonAsync(url, new { }); }
}" + Vendors);

    [Test]
    public Task LoopbackHost_DoesNotFlag() => Verify.Engine(@"
using System.Net.Http;
using System.Threading.Tasks;
public class LocalActivity {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() { await _http.GetAsync(""http://localhost:3000/api/v1/agents/x/resolve""); }
}" + Vendors);

    [Test]
    public Task InjectingTammaApiClient_DoesNotFlag() => Verify.Engine(@"
namespace Tamma.Activities.LlmCall { public class TammaApiClient { } }
public class ThinActivity {
    private readonly Tamma.Activities.LlmCall.TammaApiClient _api;
    public ThinActivity(Tamma.Activities.LlmCall.TammaApiClient api) { _api = api; }
}" + Vendors);

    // ---------- exemptions (design §5.3) → no diagnostic --------------------------------

    [Test]
    public Task LocalTool_FileReadTool_DoesNotFlag() => Verify.Engine(@"
namespace Tamma.Activities.LlmCall.Tools {
  using System.Net.Http; using System.Net.Http.Json; using System.Threading.Tasks;
  public class FileReadTool {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() { await _http.PostAsJsonAsync(""https://api.github.com/x"", new { }); }
  }
}" + Vendors);

    [Test]
    public Task InboundWebhookRegistry_WithVendorCtor_DoesNotFlag() => Verify.Engine(@"
namespace Tamma.Activities.AgentDispatch {
  public class WebhookSignalRegistry {
    public WebhookSignalRegistry(Octokit.IGitHubClient gh) { }
  }
}" + Vendors);

    [Test]
    public Task InlineToolLoopRunner_ApiCoreCoLocated_DoesNotFlag() => Verify.Engine(@"
namespace Tamma.Activities.LlmCall {
  using System.Net.Http; using System.Net.Http.Json; using System.Threading.Tasks;
  public class InlineToolLoopRunner {
    private readonly HttpClient _http = new HttpClient();
    public InlineToolLoopRunner(Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver r) { }
    public async Task Run() { await _http.PostAsJsonAsync(""https://api.anthropic.com/v1/messages"", new { }); }
  }
}" + Vendors);

    [Test]
    public Task CliAgentProviderImpl_DoesNotFlag() => Verify.Engine(@"
using System.Net.Http;
using System.Threading.Tasks;
namespace Tamma.Providers { public interface ICLIAgentProvider { } }
public class LocalCliAgent : Tamma.Providers.ICLIAgentProvider {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() { await _http.GetAsync(""https://api.example.com/x""); }
}" + Vendors);

    // ---------- non-engine surface (AC / testing #6) → no diagnostic --------------------

    [Test]
    public Task SameDirectVendorCall_InTammaApi_DoesNotFlag() => Verify.Api(@"
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
public class ApiCaller {
    private readonly HttpClient _http = new HttpClient();
    public async Task Run() { await _http.PostAsJsonAsync(""https://api.github.com/x"", new { }); }
}" + Vendors);

    [Test]
    public Task VendorInjection_InTammaApi_DoesNotFlag() => Verify.Api(@"
public class ApiService {
    public ApiService(Octokit.IGitHubClient client) { }
}" + Vendors);

    // ---------- descriptor severity / id / category (AC6, testing #7 & #10) -------------

    [Test]
    public void Descriptor_Is_Error_Tamma001_Architecture()
    {
        var d = GuardrailDiagnostics.EngineDirectExternalCall;
        Assert.That(d.Id, Is.EqualTo("TAMMA001"));
        Assert.That(d.DefaultSeverity, Is.EqualTo(DiagnosticSeverity.Error));
        Assert.That(d.Category, Is.EqualTo("Tamma.Architecture"));
        Assert.That(d.IsEnabledByDefault, Is.True);
        Assert.That(d.HelpLinkUri, Is.Not.Empty);

        var supported = new EngineExternalCallAnalyzer().SupportedDiagnostics;
        Assert.That(supported.Any(x => x.Id == "TAMMA001"), Is.True);
    }
}
