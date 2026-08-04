# Singleton `IProviderCredentialResolver` captures a scoped `IEventRepository`, so `IInlineToolLoopRunner` cannot resolve under `ValidateScopes=true`

- **Date:** 2026-07-31
- **Status:** OPEN — needs an owner
- **Found by:** adversarial review of commit `3b738cc` (Epic 43 F11 lane). **Pre-existing and
  out of that lane** — recorded here rather than fixed, so it is not lost.
- **Severity:** medium. No production symptom today (the API host does not run with
  `ValidateScopes = true`), but it is a captive-dependency defect: the singleton pins one
  request's `DbContext` for the process lifetime, and it blocks any test or host that wants
  scope validation on.

## The defect

`src/Tamma.Api/Extensions/ProviderCredentialServiceCollectionExtensions.cs:65`

```csharp
services.TryAddSingleton<IProviderCredentialResolver>(sp =>
    new DefaultProviderCredentialResolver(
        sp.GetRequiredService<ITenantProviderKeyReader>(),
        sp.GetService<IRuntimeSecretResolver>(),
        sp.GetRequiredService<IPlatformFallbackPolicy>(),
        sp.GetRequiredService<IEventRepository>(),   // <-- SCOPED, pulled from the ROOT provider
        ...));
```

`IEventRepository` is registered `AddScoped` (`src/Tamma.Data/DependencyInjection.cs:193`).
The factory above runs against the **root** service provider, so:

1. With `ValidateScopes = false` (the current API host default) the root provider hands out a
   scoped `EventRepository` anyway. It — and the `TammaDbContext` behind it — is then **captive**
   in a singleton for the life of the process: one request's `DbContext` used by every subsequent
   caller, with the usual consequences (no connection pooling, a change tracker that never
   resets, thread-safety hazards on a type that is not thread-safe).
2. With `ValidateScopes = true`, resolution **throws**:
   `Cannot resolve scoped service 'Tamma.Data.Repositories.IEventRepository' from root provider.`

## Why it surfaced in the Epic 43 lane

`InlineToolLoopRunner`'s constructor takes `IProviderCredentialResolver?` **directly**
(`src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`). The parameter is optional, but DI still
runs the singleton factory to satisfy it, so **`IInlineToolLoopRunner` cannot be resolved from a
host built with `ValidateScopes = true`** — which is the natural way to write an integration test
for the tool-loop autonomy gate's audit path end to end. Any such test has to either turn scope
validation off (and inherit the captive dependency) or hand-build the runner.

## Evidence

**Reproduced by execution**, 2026-07-31, with a throwaway NUnit probe in `Tamma.Activities.Tests`:

```csharp
var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
services.AddSingleton<ITammaModeProvider, TammaModeProvider>();
services.AddScoped<IEventRepository, FakeRepo>();      // the real lifetime
services.AddProviderCredentialResolution();            // the registration under test

var sp = services.BuildServiceProvider(validateScopes: true);
using var scope = sp.CreateScope();
scope.ServiceProvider.GetService<IProviderCredentialResolver>();
```

```
InvalidOperationException: Cannot resolve scoped service
'Tamma.Data.Repositories.IEventRepository' from root provider.
```

(The probe was deleted after the run — it belongs to whoever fixes this, as the regression test.)

Supporting:

- The registration itself, quoted above (`ProviderCredentialServiceCollectionExtensions.cs:65`).
- `services.AddScoped<IEventRepository, EventRepository>();` — `src/Tamma.Data/DependencyInjection.cs:193`.
- The comment immediately above the registration explains why the resolver is a **singleton**
  ("so the BYOK cache is process-wide") and why the factory shape exists (`IRuntimeSecretResolver`
  is optional) — neither reason accounts for taking a scoped dependency, so this looks like an
  oversight rather than a considered trade.
- Contrast with the deliberate handling elsewhere in the same tree, e.g.
  `AlertServiceCollectionExtensions.cs:66-71`, which explicitly notes "the inner
  `IEventRepository` is scoped" and works around it rather than capturing it.

## Suggested fix (for whoever picks this up)

Keep the resolver a singleton — the process-wide BYOK cache is the point — but stop capturing the
repository. Two shapes already used in this codebase:

- Inject `IServiceScopeFactory` and open a scope per audit append (the pattern
  `AlertServiceCollectionExtensions` uses), or
- Inject a `Func<IEventRepository>` / a small `IEventAppender` façade that is itself a singleton
  and resolves the scoped repository per call.

Then turn `ValidateScopes = true` on in the test host and add the resolution smoke test that would
have caught this — the defect is only invisible because nothing asserts the graph is scope-clean.

## Not to be confused with

`.dev/bugs/2026-07-29-ef-migrator-service-provider-explosion.md` — different symptom, also DI, but
that one is about migrator construction, not captive scoped dependencies.
