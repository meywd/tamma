---
title: "Epic 14: Custom ELSA Studio"
sidebar:
  order: 14
---

**Status:** Done. All 3 stories landed (14-1, 14-2, 14-3).
**Stories:** 3 (14-1..14-3).
**Primary code:** `apps/tamma-elsa/src/Tamma.Studio/` (Blazor WASM project), `Dockerfile`, `nginx.conf`.

## Overview

Epic 14 replaces the upstream `elsa-studio-v3-5` Docker image with a Tamma-branded, Tamma-extensible Blazor WASM project. ELSA Studio ships as a set of NuGet packages; the default way to "run" it is the prebuilt Docker image, which ties Tamma to whatever the upstream team picks as a default logo, theme, and UI hint set. Once the Tamma workflows grew custom activity types (LlmCall with tool loop, sanitization gates, provider resolver), the need for custom UI hint handlers and menu items outstripped what the default image could give us — so Epic 14 stands up a first-class Studio project that can be customized.

The project scaffold (14-1) is a Blazor WASM site referencing the ELSA Studio 3.5.3 NuGet packages, pinning Tamma's theme palette (primary `#7B61FF` purple) and branding (app title, logo, favicon). Story 14-2 wraps that in a 30 MB nginx-served Docker image with a CI pipeline that pushes to GHCR on every release, keeping the Studio in lockstep with the ELSA server it talks to. Story 14-3 adds custom UI hint handlers — a JSON editor for workflow JSON inputs (replacing a single-line text field that was brutal to edit) and a provider-selector dropdown for multi-provider configuration.

The result: Tamma's Studio looks and feels like part of Tamma, can display custom UI affordances for Tamma-specific activities, and remains a thin wrapper around upstream ELSA Studio so every upstream feature flows through automatically.

## Architecture

```
+-----------------------------------------------------------------+
|           Tamma.Studio (Blazor WebAssembly, net8.0)             |
|-----------------------------------------------------------------|
|  Program.cs                                                     |
|    AddCore() → AddShell() → AddRemoteBackend() →                |
|    AddWorkflowsModule() → AddDashboardModule()                  |
|    Singleton: IBrandingProvider  → TammaBrandingProvider        |
|    Singleton: IThemeProvider     → TammaThemeProvider           |
|    Singleton: IUIHintHandler     → JsonEditorUIHintHandler      |
|    Singleton: IUIHintHandler     → ProviderSelectorUIHintHandler|
|                                                                 |
|  Components/                                                    |
|    Custom pages + overrides                                     |
|  Branding/                                                      |
|    TammaBrandingProvider.cs                                     |
|  Theming/                                                       |
|    TammaThemeProvider.cs (MudBlazor palette)                    |
|  UIHints/                                                       |
|    JsonEditorUIHintHandler.cs                                   |
|    ProviderSelectorUIHintHandler.cs                             |
|  Navigation/                                                    |
|    TammaMenuProvider.cs (custom menu items)                     |
|  wwwroot/                                                       |
|    logo.svg, favicon.ico, tamma-overrides.css                   |
|                                                                 |
|  -> talks to ELSA Server via AddRemoteBackend(options.Url)      |
+-----------------------------------------------------------------+
          |                               |                |
          |   (static WASM assets)        |                |
          v                               v                v
+-------------------+          +-------------------+   +---------------+
| nginx container   |          |  ELSA Server      |   |   GHCR        |
| serves WASM +     | <------> |  (apps/tamma-elsa |   | ghcr.io/meywd/|
| proxies API       |          |   /Tamma.ElsaServer)  | tamma-studio  |
|                   |          |                   |   |               |
| Dockerfile (~30MB)|          |                   |   | pushed by CI  |
+-------------------+          +-------------------+   +---------------+
```

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `Tamma.Studio.csproj` | Blazor WASM project referencing ELSA Studio 3.5.3 packages | `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj` | 14-1 / Done |
| `Program.cs` | Studio bootstrap — registers modules + providers | `apps/tamma-elsa/src/Tamma.Studio/Program.cs` | 14-1 / Done |
| `TammaBrandingProvider` | `IBrandingProvider` — app title, logo URL, favicon, primary color | `Branding/TammaBrandingProvider.cs` | 14-1 / Done |
| `TammaThemeProvider` | MudBlazor `MudTheme` — purple primary, emerald secondary, dark mode | `Theming/TammaThemeProvider.cs` | 14-1 / Done |
| Static assets | Logo, favicon, CSS overrides | `wwwroot/logo.svg`, `wwwroot/favicon.ico`, `wwwroot/tamma-overrides.css` | 14-1 / Done |
| `Dockerfile` | Multi-stage build: publish WASM, copy to nginx:alpine | `apps/tamma-elsa/src/Tamma.Studio/Dockerfile` | 14-2 / Done |
| `nginx.conf` | Serves static WASM, fallback routing, gzip | `apps/tamma-elsa/src/Tamma.Studio/nginx.conf` | 14-2 / Done |
| `docker-entrypoint.sh` | Injects runtime `ElsaServer:Url` into `appsettings.json` at start | `docker-entrypoint.sh` | 14-2 / Done |
| CI workflow | Builds + pushes image to GHCR on release tag | `.github/workflows/publish-studio.yml` | 14-2 / Done |
| `JsonEditorUIHintHandler` | Monaco/CodeMirror JSON editor for workflow JSON input fields | `UIHints/JsonEditorUIHintHandler.cs` | 14-3 / Done |
| `ProviderSelectorUIHintHandler` | Multi-select provider dropdown backed by config API | `UIHints/ProviderSelectorUIHintHandler.cs` | 14-3 / Done |
| Custom menu items | Tamma-specific navigation entries (Sessions, Diagnostics) | `Navigation/TammaMenuProvider.cs` | 14-3 / Done |
| `[UIHint]` attributes | Applied on activity inputs in `Tamma.Activities/*` to opt into custom UI hints | Across activity classes | 14-3 / Done |

## Class / type structure

```
apps/tamma-elsa/src/Tamma.Studio/
  Program.cs
    - WebAssemblyHostBuilder setup
    - AddCore / AddShell / AddRemoteBackend(url) / AddWorkflowsModule / AddDashboardModule
    - Singleton registration of branding, theme, UI hint handlers, menu provider

  Branding/TammaBrandingProvider.cs
    class TammaBrandingProvider : IBrandingProvider
      string AppTitle = "Tamma Studio"
      string LogoUrl = "_content/Tamma.Studio/logo.svg"
      string FaviconUrl = "_content/Tamma.Studio/favicon.ico"
      string PrimaryColor = "#7B61FF"

  Theming/TammaThemeProvider.cs
    class TammaThemeProvider : IThemeProvider
      MudTheme Theme =>
        Palette { Primary = #7B61FF, Secondary = #10b981, ... }
        PaletteDark { ... }
        Typography { ... }

  UIHints/JsonEditorUIHintHandler.cs
    class JsonEditorUIHintHandler : IUIHintHandler
      string UIHint => "tamma:json-editor"
      RenderFragment Render(PropertyDescriptor prop, IServiceProvider svc)
      // renders a Monaco/CodeMirror component with JSON validation

  UIHints/ProviderSelectorUIHintHandler.cs
    class ProviderSelectorUIHintHandler : IUIHintHandler
      string UIHint => "tamma:provider-selector"
      RenderFragment Render(...)
      // fetches provider list from /api/v1/providers/list; multi-select

  Navigation/TammaMenuProvider.cs
    class TammaMenuProvider : IMenuProvider
      IEnumerable<MenuItem> GetMenuItems()
      // adds: Sessions, Diagnostics, Prompt Store, Agent Config

Example attribute usage in activities:
  apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs
    [UIHint("tamma:provider-selector")]
    public Input<string> ProviderName { get; set; }

    [UIHint("tamma:json-editor")]
    public Input<string> SystemPromptVariables { get; set; }
```

## Sequence — operator edits an LLM activity in Studio

```
Operator       Tamma.Studio (Blazor WASM)          ELSA Server          Config API (Epic 9)      Providers
   |                  |                                   |                        |                   |
   | open Studio ---> |                                   |                        |                   |
   |                  | load shell + workflows module     |                        |                   |
   |                  | GET /api/workflows ------------> |                        |                   |
   |                  | <-- list                                                                           |
   |                  | operator clicks CallLlmInlineActivity                                              |
   |                  | activity property editor renders:                                                  |
   |                  |   - ProviderName [UIHint="tamma:provider-selector"]                                |
   |                  |     -> ProviderSelectorUIHintHandler.Render                                        |
   |                  |     -> GET /api/v1/providers/list ---------------->                                |
   |                  |     <------ [claude-code, openrouter, opencode, ...]                               |
   |                  |     renders <MudSelect> with provider options                                      |
   |                  |   - SystemPromptVariables [UIHint="tamma:json-editor"]                             |
   |                  |     -> JsonEditorUIHintHandler.Render                                              |
   |                  |     renders Monaco component with JSON schema validation                          |
   |                  | operator edits values                                                              |
   |                  | PUT /api/workflows/:id --------------> save workflow definition                   |
   |                  | <-- saved                         |                        |                   |
```

## Use cases

- **Tamma-branded workflow UI** — operators see the Tamma logo, purple theme, and "Tamma Studio" title instead of a generic ELSA shell.
- **JSON input fields for complex workflow variables** — activities that need structured JSON inputs (`SystemPromptVariables`, `ToolLoopConfig`, `ContextFilter`) get a syntax-highlighted editor with schema validation instead of a raw multi-line textbox.
- **Provider selection via dropdown** — LLM activities display a live provider list fetched from the Epic 9 config API; no more typo-risking a provider name into a free-text field.
- **Custom menu items for Tamma features** — sidebar includes entries for Tamma-specific concerns (Sessions, Diagnostics, Prompt Store, Agent Config) alongside the default ELSA tabs.
- **Deployed alongside the stack** — Studio image (~30 MB) ships as a sibling service in the Tamma Compose file, accessible at `elsa.tamma.dev` (or local `:9000`), automatically rolled forward with each release.

## Dependencies

**Upstream**
- ELSA Studio 3.5.3 (NuGet packages `Elsa.Studio`, `Elsa.Studio.Core.BlazorWasm`, `Elsa.Studio.Shell`, `Elsa.Studio.Workflows`, `Elsa.Studio.Dashboard`, `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm`).
- Epic 7 — the ELSA server the Studio talks to and the workflows it visualizes.
- Epic 9 — `/api/v1/providers/list`, `/agents/config` endpoints consumed by `ProviderSelectorUIHintHandler`.
- Epic 10 — events the Studio renders (when Story 10-9 activity events land, Studio picks them up automatically through the ELSA reporting surface).

**Downstream**
- Epic 8 — Tier 3 Docker stack adds Studio as the seventh service.
- Epic 16 — unified auth; Studio will move from ELSA Identity to Tamma GitHub OAuth once Epic 16 lands.

## Current state

Landed:
- `f421dd3 feat(studio): scaffold custom Tamma Studio Blazor WASM project [14-1]`
- `a313ab8 feat(studio): Dockerfile, docker-compose, CI for custom Studio [14-2]`
- `145ecbd feat(studio): custom UI hints, menu items, UIHint attributes [14-3]`

Deploy status:
- Image `ghcr.io/meywd/tamma-studio:latest` built multi-arch (amd64 + arm64).
- Runs as part of `docker-compose.yml` from Epic 8, reachable at `elsa.tamma.dev`.
- Handshake with ELSA server configured via `ElsaServer:Url` env var, baked into `appsettings.json` at container start by `docker-entrypoint.sh`.

Stubs / deferrals:
- Auth currently piggybacks on ELSA Identity; Epic 16 brings unified GitHub OAuth SSO. Tracked, not Epic 14.
- Activity-specific visualizations (e.g. live token-stream preview inside `CallLlmInlineActivity`) are a future enhancement — infrastructure is in place via `IUIHintHandler` but the handlers themselves are not part of Epic 14.
- Dashboard tab shows default ELSA widgets; Tamma-specific widgets (cost per workflow, retry-loop health) are planned follow-ups.

## See also

- [Epic 7: Mentorship](Epic-7-Mentorship.md) — workflows the Studio visualizes.
- [Epic 8: Distribution](Epic-8-Distribution.md) — Tier 3 Compose file includes Studio.
- [Epic 9: Agent Management](Epic-9-Agent-Management.md) — config API the provider selector calls.
- [Epic 10: Engine Core](Epic-10-Engine-Core.md) — events + workflow provider abstraction behind Studio.
- [Epic 13: Workflow Decomposition](Epic-13-Workflow-Decomposition.md) — decomposed sub-workflows render cleanly here.
- [Epic 16: Auth & Admin](Epic-16-Auth-Admin.md) — future unified auth for Studio.
- Source plan: `.dev/plans/elsa-studio-customization.md`.
- Impl plans: [`docs/stories/epic-14/`](/stories/epic-14/).
- Source: `apps/tamma-elsa/src/Tamma.Studio/`.

---

_Last refreshed 2026-04-22._
