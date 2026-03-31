# Plan: Custom ELSA Studio via Composition

## Summary
New Blazor WASM project referencing ELSA Studio NuGet packages. Custom branding, theme, Docker image. Replaces upstream `elsa-studio-v3-5` image. ~13-20 hours total, MVP in ~8-11 hours.

## Phase 1: Scaffold Project (2-3 hours)
- New: `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj` — Blazor WASM, net8.0
- NuGet refs: Elsa.Studio, Elsa.Studio.Core.BlazorWasm, Elsa.Studio.Shell, Elsa.Studio.Workflows, Elsa.Studio.Dashboard, Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm (all 3.5.3)
- Program.cs: AddCore(), AddShell(), AddRemoteBackend(), AddWorkflowsModule(), AddDashboardModule()
- wwwroot/index.html, appsettings.json, App.razor, _Imports.razor
- Add to Tamma.sln

## Phase 2: Branding + Theme (2-3 hours)
- New: `Branding/TammaBrandingProvider.cs` — implements IBrandingProvider
  - AppTitle: "Tamma Studio", LogoUrl, FaviconUrl, PrimaryColor: #7B61FF
- New: `Theming/TammaThemeProvider.cs` — MudTheme with purple palette
  - Primary: #7B61FF, Secondary: #10b981, dark mode variant
- Static assets: logo.svg (recolor from marketing-site), favicon.ico
- CSS: tamma-overrides.css for canvas/node colors

## Phase 3: Dockerfile + Docker Compose (2-3 hours)
- New: `Tamma.Studio/Dockerfile` — multi-stage: dotnet SDK build → nginx:alpine runtime
  - Blazor WASM = static files, served by nginx (~30MB image)
- New: `nginx.conf` — SPA routing, gzip for .wasm/.dll/.js, cache headers
- New: `docker-entrypoint.sh` — envsubst for ELSASERVER__URL into appsettings.json
- Modify: `docker/docker-compose.yml` — replace `image: elsaworkflows/elsa-studio-v3-5:latest` with `build: context + dockerfile`

## Phase 4: CI Integration (1-2 hours)
- Modify: `.github/workflows/docker-publish.yml`
  - Add to build-dotnet matrix: `{ name: tamma-studio, context: apps/tamma-elsa/src, dockerfile: .../Tamma.Studio/Dockerfile }`
  - Add to docker-compose.images.yml: `elsa-studio: image: ghcr.io/.../tamma-studio:${TAG}`

## Phase 5: Custom Menu Items (1-2 hours)
- New: `Navigation/TammaMenuProvider.cs` — implements IMenuProvider
  - ADL Dashboard, LLM Diagnostics, Mentorship Sessions links
  - Initially link to filtered workflow instance views

## Phase 6: Custom UI Hint Handlers (3-4 hours)
- New: `UIHints/JsonEditorUIHintHandler.cs` — "tamma-json-editor" for JSON inputs
- New: `UIHints/ProviderSelectorUIHintHandler.cs` — "tamma-provider-selector" multi-select
- Modify activities to add UIHint attributes:
  - CallLlmActivity: ToolsJson → "tamma-json-editor"
  - WaitForPlanApprovalActivity: PlanJson → "tamma-json-editor"
  - ResolveLlmPromptActivity: SystemPromptOverride → "multi-line"

## Phase 7: Custom Activity Tabs (deferred, 2-3 hours)
- LLM Call Diagnostics Tab — recent call stats on CallLlm activities
- Workflow Lineage Tab — parent workflow context on ADL activities

## Dependencies
Phase 1 → Phase 2 → Phase 3 → Phase 4 (MVP)
Phase 3 → Phase 5 + Phase 6 (parallel)

## Key Risks
- NuGet version mismatch → pin all to 3.5.3
- WASM asset size (15-30MB) → Brotli compression + cache headers
- Env var injection → docker-entrypoint.sh rewrites appsettings.json
- MudBlazor version → let NuGet resolve transitively, don't pin explicitly
