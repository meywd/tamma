# Story 14.1: Studio Blazor WASM Scaffold

Status: ready-for-dev

## Story

As a **platform engineer**,
I want a custom Blazor WASM project that references ELSA Studio NuGet packages with Tamma branding and a purple-themed MudBlazor palette,
so that Tamma has its own branded Studio instead of the generic upstream `elsa-studio-v3-5` Docker image, enabling future customization of menus, UI hints, and activity tabs.

## Acceptance Criteria

1. New project `Tamma.Studio.csproj` exists at `apps/tamma-elsa/src/Tamma.Studio/` as a Blazor WASM project targeting `net8.0`
2. NuGet references pinned to ELSA Studio 3.5.3: `Elsa.Studio`, `Elsa.Studio.Core.BlazorWasm`, `Elsa.Studio.Shell`, `Elsa.Studio.Workflows`, `Elsa.Studio.Dashboard`, `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm`
3. `Program.cs` calls `AddCore()`, `AddShell()`, `AddRemoteBackend()`, `AddWorkflowsModule()`, `AddDashboardModule()` for full Studio functionality
4. `TammaBrandingProvider` implements `IBrandingProvider` with: AppTitle = "Tamma Studio", LogoUrl pointing to Tamma logo, FaviconUrl, PrimaryColor = `#7B61FF`
5. `TammaThemeProvider` provides a MudBlazor `MudTheme` with: Primary = `#7B61FF`, Secondary = `#10b981`, dark mode variant
6. Static assets include: `logo.svg` (Tamma logo), `favicon.ico`, `tamma-overrides.css`
7. `wwwroot/index.html` references the custom CSS and branding assets
8. `appsettings.json` configures the ELSA Server URL (externalized for Docker env var injection)
9. Project added to `Tamma.sln`
10. Project builds successfully with `dotnet build` and produces a working Blazor WASM app
11. Studio connects to the existing ELSA Server and displays workflows with Tamma branding

## Technical Context

### ELSA Studio Architecture

ELSA Studio is a Blazor WASM application distributed as NuGet packages. To customize it, you create a new Blazor WASM project that references the packages and registers custom providers:

```csharp
// Program.cs
var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");

builder.Services.AddCore();
builder.Services.AddShell();
builder.Services.AddRemoteBackend(options =>
{
    options.Url = new Uri(builder.Configuration["ElsaServer:Url"]!);
});
builder.Services.AddWorkflowsModule();
builder.Services.AddDashboardModule();

// Custom branding
builder.Services.AddSingleton<IBrandingProvider, TammaBrandingProvider>();

await builder.Build().RunAsync();
```

### Branding Provider

```csharp
public class TammaBrandingProvider : IBrandingProvider
{
    public string AppTitle => "Tamma Studio";
    public string LogoUrl => "_content/Tamma.Studio/logo.svg";
    public string FaviconUrl => "_content/Tamma.Studio/favicon.ico";
    public string PrimaryColor => "#7B61FF";
}
```

### Theme Provider

```csharp
public class TammaThemeProvider
{
    public MudTheme Theme => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#7B61FF",
            Secondary = "#10b981",
            AppbarBackground = "#1a1a2e",
            Background = "#fafafa",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#9B85FF",
            Secondary = "#34d399",
            AppbarBackground = "#0f0f1e",
            Background = "#121212",
        }
    };
}
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj`
- `apps/tamma-elsa/src/Tamma.Studio/Program.cs`
- `apps/tamma-elsa/src/Tamma.Studio/App.razor`
- `apps/tamma-elsa/src/Tamma.Studio/_Imports.razor`
- `apps/tamma-elsa/src/Tamma.Studio/wwwroot/index.html`
- `apps/tamma-elsa/src/Tamma.Studio/wwwroot/appsettings.json`
- `apps/tamma-elsa/src/Tamma.Studio/wwwroot/css/tamma-overrides.css`
- `apps/tamma-elsa/src/Tamma.Studio/wwwroot/logo.svg`
- `apps/tamma-elsa/src/Tamma.Studio/wwwroot/favicon.ico`
- `apps/tamma-elsa/src/Tamma.Studio/Branding/TammaBrandingProvider.cs`
- `apps/tamma-elsa/src/Tamma.Studio/Theming/TammaThemeProvider.cs`

### Files to Modify

- `apps/tamma-elsa/Tamma.sln` — add Tamma.Studio project

### Key Risks

- **NuGet version mismatch**: All ELSA Studio packages must be pinned to the same version (3.5.3) as the ELSA Server packages. Version mismatches cause runtime JS interop failures.
- **WASM asset size**: Blazor WASM downloads 15-30MB of .NET assemblies on first load. Brotli compression + cache headers (handled in Story 14.2's nginx config) mitigate this.
- **MudBlazor version**: Let NuGet resolve MudBlazor transitively through ELSA Studio packages. Do not pin MudBlazor explicitly — version conflicts are common.

## Implementation Notes

1. Start with `dotnet new blazorwasm -n Tamma.Studio` in the `apps/tamma-elsa/src/` directory, then add the ELSA Studio NuGet references.
2. Verify NuGet restore succeeds and all packages resolve without conflicts before adding any custom code.
3. The `appsettings.json` should use a placeholder URL: `"ElsaServer": { "Url": "http://localhost:13000" }`. Docker entrypoint (Story 14.2) will overwrite this with the real URL.
4. The `logo.svg` can be the existing Tamma logo from the marketing site (if available) or a simple SVG placeholder. The visual design is less important than the technical scaffold.
5. `tamma-overrides.css` should contain minimal overrides: workflow canvas background color, node header colors matching the purple theme.
6. Test by running: `dotnet run --project apps/tamma-elsa/src/Tamma.Studio` and verifying the app loads in a browser, connects to the ELSA Server, and displays the Tamma branding.

## Testing Strategy

- **Build verification**: `dotnet build` succeeds with no errors or warnings
- **NuGet restore**: All packages resolve without version conflicts
- **Manual verification**: Launch the app, verify branding (logo, title, colors) appears
- **Connectivity test**: App connects to ELSA Server and loads workflow definitions
- **Theme verification**: Light and dark modes display correct colors
- **No automated tests for this story** (Blazor WASM testing requires bUnit + complex setup; covered by visual inspection for MVP)

## Dependencies

- **None** (foundational story for Epic 14)

## Estimated Effort

2-3 days (including NuGet troubleshooting)

## Logging Requirements

### Existing Coverage

The story has **no logging requirements** specified. As a Blazor WASM application running in the browser, traditional server-side ILogger is not directly applicable. However, Blazor WASM has its own `ILogger` infrastructure that logs to the browser console.

### Required Additions

`Program.cs` should configure Blazor WASM logging. Custom providers should use `ILogger<T>` where available.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| Studio application started | INFO | `{ElsaServerUrl}`, `{AppVersion}` | Logged during `Program.cs` initialization — confirms the backend URL is configured |
| Branding provider loaded | DEBUG | `{AppTitle}`, `{PrimaryColor}` | Confirms TammaBrandingProvider is active |
| Theme provider loaded | DEBUG | `{ThemeMode}` ("light" or "dark") | Confirms TammaThemeProvider is active |
| Backend connection established | INFO | `{ElsaServerUrl}`, `{ConnectionDurationMs}` | First successful API call to the ELSA Server |
| Backend connection failed | ERROR | `{ElsaServerUrl}`, `{ErrorMessage}` | Studio cannot reach the ELSA Server |

### Sensitive Data Redaction

- Do NOT log API keys or authentication tokens in the browser console.
- The `ElsaServerUrl` is safe to log (it is a URL, not a secret).

### Correlation IDs

- Blazor WASM does not participate in server-side workflow correlation. No `WorkflowInstanceId` needed.
- Consider adding a `{SessionId}` (generated at startup) for correlating browser console logs during a user session.

### Note on Log Priority

This story has the **lowest logging priority** in the audit. It is a UI scaffold with no security or workflow implications. The 5 log statements above are sufficient for MVP.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/elsa-studio-customization.md` Phases 1+2 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
