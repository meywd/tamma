# Testing

Tamma has a large, fast test baseline. The auth-foundation sprint brought the C# test suite up to **1700+ NUnit test methods across 135 files** in `apps/tamma-elsa/tests/`, covering auth, orgs, providers, prompts, engine, github, kb, agent-dispatch, and provisioning.

## Test projects

```
apps/tamma-elsa/tests/
├── Tamma.Activities.Tests/    # Elsa activities + workflows (~260 tests)
│   ├── ADL/                  # AdlActivityTests, AdlModelsTests
│   ├── AgentDispatch/        # Epic 19: factory, local, actions, services, activities
│   ├── AI/                   # Claude, context, suggestion
│   ├── LlmCall/              # agentic loop, tools, security
│   ├── Security/             # sanitizer, error-redactor, action-gate, prompt-hardening
│   └── Workflows/            # Single issue cycle, CI retry, plan review, etc.
├── Tamma.Api.Tests/           # REST API surface (~900 tests)
│   ├── Agents/               # AgentResolverService, RolePhaseMap
│   ├── Auth/                 # password, JWT, rate limit, OAuth state codec
│   ├── Conventions/          # language/framework convention templates
│   ├── Diagnostics/          # budget, aggregation
│   ├── Email/                # register, password-reset, outbox, SMTP, Resend
│   ├── Engine/               # SSE lifecycle, registry heartbeat, task queue
│   ├── GitHub/               # Octokit clients, libsodium, install router
│   ├── Infrastructure/       # ConnectionStringResolver
│   ├── KnowledgeBase/        # endpoints, intelligence HTTP client
│   ├── Logging/              # LogSanitizer
│   ├── Orgs/                 # role hierarchy, delete, membership filter, slug validation
│   ├── PromptStore/          # resolution order, render, events
│   ├── Providers/            # circuit breaker, chain resolver, provider health
│   ├── ProviderSession/      # session lifecycle, pricing, cleanup
│   ├── Provisioning/         # Cranl client, workflow, provisioner state machine
│   ├── SaaS/                 # LLM proxy, API key rotation, workflow lifecycle
│   ├── Sanitization/         # content sanitizer, regex timeouts
│   ├── Tenancy/              # query filter + interceptor integration
│   └── TaskQueue/            # DbTaskQueue, processor, repository, webhook integration
└── Tamma.Core.Tests/          # Pure-domain (enums, state machines)
```

Attribute: **NUnit** `[TestFixture]` + `[Test]` / `[TestCase]` (not xUnit).

## Running locally

```bash
cd apps/tamma-elsa
dotnet test

# Single project:
dotnet test tests/Tamma.Api.Tests

# Filter to a class:
dotnet test --filter "FullyQualifiedName~ContentSanitizerTests"
```

## Testcontainers pattern

Integration tests that need Postgres (tenancy, schema isolation, migrations, real repositories) use **Testcontainers for .NET**:

```csharp
[TestFixture]
public class QueryFilterAndInterceptorTests
{
    private PostgreSqlContainer _db = null!;

    [OneTimeSetUp]
    public async Task StartContainer()
    {
        _db = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithCleanUp(true)
            .Build();
        await _db.StartAsync();
        // Run migrations, seed tenants, etc.
    }

    [OneTimeTearDown]
    public Task StopContainer() => _db.DisposeAsync().AsTask();
}
```

The same pattern covers the Phase-3 dual-connection tests (admin vs `tamma_app` role) — one container, two connection strings.

## Env-var hygiene in tests

Test fixtures now clean `ConnectionStrings__TammaAppDb` / `TammaDb` at `OneTimeTearDown` so one test's env override can't leak into the next (infra hardening landed this sprint).

## Mocking patterns

- `Moq` for interfaces (e.g. `Mock<IGitHubAppClient>`).
- `NSubstitute` occasionally (legacy code paths).
- For Octokit: the `IGitHubAppClient` contract is narrow by design — mock it, don't mock Octokit directly.
- For the agent executor: `IProcessRunner` (LocalExecutor) and `IGitHubActionsClient` (GitHubActionsExecutor) are both swappable; tests substitute deterministic fakes.

## What's not covered

- KB scope (vector DB wiring) has contract-level coverage only — backend paths (Chroma, pgvector, Pinecone, Qdrant, Weaviate) are mocked. Real backend smoke tests are deferred behind Epic 6 finish.
- Voice conversation (Epic 24) has no tests yet — epic is drafted, not implemented.
- Multi-pod Redis rate limiter has unit tests for the Lua script contract but no cross-pod integration yet — deferred until the second pod is deployed.

## CI

`.github/workflows/ci.yml` runs `dotnet test` + `pnpm test` + `pnpm lint` on every PR. Docker smoke test (`docker-smoke-test.yml`) stands up the full compose stack and hits a handful of endpoints.

## Related

- [Security](Security)
- [Deployment → least-privilege app-role runbook](Deployment#least-privilege-app-role-runbook)
- [Port Audit](Port-Audit) — most findings landed with their own tests
