---
title: "Story 14.1: Studio Blazor WASM Scaffold — Implementation Plan"
sidebar:
  order: 140
---

## Overview

Create a custom Blazor WASM project `Tamma.Studio` that references ELSA Studio NuGet packages, registers Tamma branding and purple theme, and replaces the upstream generic `elsa-studio-v3-5` Docker image as the foundation for all future Studio customization.

**Build context**: `apps/tamma-elsa/src` (same as Tamma.Api Dockerfile)
**Target path**: `apps/tamma-elsa/src/Tamma.Studio/`

---

## Step-by-Step Implementation Tasks

### Step 1: Create the Blazor WASM Project File

Create `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj`.

Do NOT use `dotnet new blazorwasm` as it pulls in unneeded template files. Create the csproj manually with exact NuGet versions matching the server (3.5.3).

**File**: `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Tamma.Studio</RootNamespace>
    <AssemblyName>Tamma.Studio</AssemblyName>
    <Version>1.0.0</Version>
    <Authors>Tamma Team</Authors>
    <Description>Custom ELSA Studio UI for Tamma — Blazor WASM with branding, theme, and custom UI hints</Description>
  </PropertyGroup>

  <ItemGroup>
    <!-- ELSA Studio packages — ALL must match server version 3.5.3 exactly -->
    <PackageReference Include="Elsa.Studio" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Core.BlazorWasm" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Shell" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Shell.BlazorWasm" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Workflows" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Dashboard" Version="3.5.3" />
    <PackageReference Include="Elsa.Studio.Login.BlazorWasm" Version="3.5.3" />

    <!-- Blazor WASM runtime -->
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="8.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="8.0.0" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

**Key decisions**:
- SDK is `Microsoft.NET.Sdk.BlazorWebAssembly` (NOT `Microsoft.NET.Sdk.Web`).
- `Elsa.Studio.Shell.BlazorWasm` provides the Blazor WASM shell host (router, layout).
- `Elsa.Studio.Login.BlazorWasm` provides the ELSA Identity login flow for WASM. The story file says `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm` but the actual NuGet package name in ELSA 3.5.x is `Elsa.Studio.Login.BlazorWasm`. Verify at NuGet restore time; if that fails, try `Elsa.Studio.Login.BlazorWasm` or check the ELSA 3.5.3 release notes.
- Do NOT pin MudBlazor explicitly; it is resolved transitively by `Elsa.Studio.Shell`.

### Step 2: Create Program.cs

**File**: `apps/tamma-elsa/src/Tamma.Studio/Program.cs`

```csharp
using Elsa.Studio.Core.BlazorWasm.Extensions;
using Elsa.Studio.Dashboard.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Login.BlazorWasm.Extensions;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Workflows.Extensions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Tamma.Studio;
using Tamma.Studio.Branding;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ELSA Studio core services
builder.Services.AddCore();
builder.Services.AddShell();
builder.Services.AddRemoteBackend(options =>
{
    options.Url = new Uri(builder.Configuration["ElsaServer:Url"]
        ?? "http://localhost:13000");
});

// ELSA Studio modules
builder.Services.AddLoginModule();
builder.Services.AddDashboardModule();
builder.Services.AddWorkflowsModule();

// Tamma branding
builder.Services.AddScoped<IBrandingProvider, TammaBrandingProvider>();

await builder.Build().RunAsync();
```

**Notes on the `IBrandingProvider` registration**:
- ELSA Studio defines `IBrandingProvider` in `Elsa.Studio.Contracts` or `Elsa.Studio.Core`. The exact namespace may vary between 3.5.x minor versions.
- If `IBrandingProvider` is not found at compile time, search the ELSA Studio NuGet packages for the interface name. It may be `Elsa.Studio.Models.IBrandingProvider` or similar.
- Registration is `AddScoped` because ELSA Studio resolves it per-circuit in Blazor.
- The `AddLoginModule()` extension comes from `Elsa.Studio.Login.BlazorWasm.Extensions`. If the package is actually named `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm`, the extension namespace will differ. Adjust imports accordingly.

**Fallback strategy if ELSA Studio API differs from expected**:
1. Run `dotnet restore` and check the restored DLLs in the NuGet cache.
2. Use `dotnet metadata` or ILSpy to inspect the actual public API surface.
3. The ELSA Studio GitHub repo at tag `v3.5.3` is the canonical source.

### Step 3: Create App.razor

**File**: `apps/tamma-elsa/src/Tamma.Studio/App.razor`

```razor
<ElsaStudioShell />
```

This is the minimal Blazor root component. `ElsaStudioShell` comes from `Elsa.Studio.Shell` and provides the full Studio layout (navigation, router, theme).

If `ElsaStudioShell` is not available in 3.5.3, the alternative is:

```razor
@using Elsa.Studio.Shell

<Router AppAssembly="@typeof(App).Assembly" AdditionalAssemblies="@_additionalAssemblies">
    <Found Context="routeData">
        <RouteView RouteData="@routeData" DefaultLayout="@typeof(MainLayout)" />
    </Found>
    <NotFound>
        <LayoutView Layout="@typeof(MainLayout)">
            <p>Page not found.</p>
        </LayoutView>
    </NotFound>
</Router>

@code {
    private readonly System.Reflection.Assembly[] _additionalAssemblies =
    {
        typeof(Elsa.Studio.Shell._Imports).Assembly,
        typeof(Elsa.Studio.Workflows._Imports).Assembly,
        typeof(Elsa.Studio.Dashboard._Imports).Assembly,
    };
}
```

### Step 4: Create _Imports.razor

**File**: `apps/tamma-elsa/src/Tamma.Studio/_Imports.razor`

```razor
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.Web.Virtualization
@using Microsoft.AspNetCore.Components.WebAssembly.Http
@using Microsoft.JSInterop
@using MudBlazor
@using Tamma.Studio
```

### Step 5: Create wwwroot/index.html

**File**: `apps/tamma-elsa/src/Tamma.Studio/wwwroot/index.html`

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Tamma Studio</title>
    <base href="/" />

    <!-- MudBlazor CSS (loaded from _content) -->
    <link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />

    <!-- ELSA Studio CSS -->
    <link href="_content/Elsa.Studio.Shell/css/shell.css" rel="stylesheet" />

    <!-- Tamma custom overrides (must be last) -->
    <link href="css/tamma-overrides.css" rel="stylesheet" />

    <!-- Favicon -->
    <link rel="icon" type="image/x-icon" href="favicon.ico" />
</head>
<body>
    <div id="app">
        <div style="display:flex;align-items:center;justify-content:center;height:100vh;font-family:sans-serif;color:#7B61FF;">
            <div style="text-align:center">
                <h2>Tamma Studio</h2>
                <p>Loading...</p>
            </div>
        </div>
    </div>

    <div id="blazor-error-ui" style="display:none;position:fixed;bottom:0;width:100%;background:#cc0000;color:white;padding:0.5em;text-align:center;">
        An unhandled error has occurred.
        <a href="" class="reload" style="color:white;text-decoration:underline;">Reload</a>
    </div>

    <!-- Blazor WASM framework -->
    <script src="_framework/blazor.webassembly.js"></script>

    <!-- MudBlazor JS -->
    <script src="_content/MudBlazor/MudBlazor.min.js"></script>
</body>
</html>
```

**Notes**:
- The `_content/Elsa.Studio.Shell/css/shell.css` path is the standard ELSA Studio shell stylesheet. If it is not found at runtime, check the NuGet package content with `dotnet publish` and inspect the `wwwroot/_content/` directory.
- The loading indicator uses Tamma purple `#7B61FF`.
- `tamma-overrides.css` is loaded last to override ELSA/MudBlazor defaults.

### Step 6: Create wwwroot/appsettings.json

**File**: `apps/tamma-elsa/src/Tamma.Studio/wwwroot/appsettings.json`

```json
{
  "ElsaServer": {
    "Url": "http://localhost:13000"
  }
}
```

The placeholder URL `http://localhost:13000` is intentionally invalid for production. The Docker entrypoint (Story 14.2) replaces this via `sed` with the `ELSASERVER__URL` environment variable.

For local dev without Docker: change to `http://localhost:5000` (the local ELSA Server port).

### Step 7: Create Branding Provider

**File**: `apps/tamma-elsa/src/Tamma.Studio/Branding/TammaBrandingProvider.cs`

```csharp
namespace Tamma.Studio.Branding;

/// <summary>
/// Provides Tamma branding for ELSA Studio: app title, logo, favicon, and primary color.
/// Registered in DI as IBrandingProvider in Program.cs.
/// </summary>
public class TammaBrandingProvider : IBrandingProvider
{
    /// <summary>Displayed in the browser tab and Studio header.</summary>
    public string AppTitle => "Tamma Studio";

    /// <summary>Logo shown in the Studio navigation sidebar.</summary>
    public string LogoUrl => "logo.svg";

    /// <summary>Browser favicon.</summary>
    public string FaviconUrl => "favicon.ico";

    /// <summary>Primary brand color — Tamma purple.</summary>
    public string PrimaryColor => "#7B61FF";
}
```

**IMPORTANT**: The `IBrandingProvider` interface lives in the ELSA Studio packages. The exact namespace must be verified at build time. Common locations:
- `Elsa.Studio.Contracts.IBrandingProvider`
- `Elsa.Studio.Models.IBrandingProvider`
- `Elsa.Studio.Core.Contracts.IBrandingProvider`

Add the correct `using` directive after `dotnet restore` by searching restored assemblies:
```bash
find ~/.nuget/packages/elsa.studio* -name "*.dll" | head -5
# Then inspect with: dotnet tool run ilspy ... or search .cs in the ELSA Studio GitHub repo
```

If the interface has additional properties beyond `AppTitle`, `LogoUrl`, `FaviconUrl`, `PrimaryColor`, implement them with sensible Tamma defaults.

### Step 8: Create Theme Provider

**File**: `apps/tamma-elsa/src/Tamma.Studio/Theming/TammaThemeProvider.cs`

```csharp
using MudBlazor;

namespace Tamma.Studio.Theming;

/// <summary>
/// Provides the Tamma MudBlazor theme with purple primary palette.
/// If ELSA Studio exposes a theme customization point (e.g. IThemeProvider),
/// register this class. Otherwise, apply the theme via CSS overrides.
/// </summary>
public static class TammaThemeProvider
{
    public static MudTheme Theme => new()
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#7B61FF",
            PrimaryDarken = "#5A3FD6",
            PrimaryLighten = "#9B85FF",
            Secondary = "#10b981",
            SecondaryDarken = "#059669",
            SecondaryLighten = "#34d399",
            AppbarBackground = "#1a1a2e",
            AppbarText = "#ffffff",
            Background = "#fafafa",
            Surface = "#ffffff",
            DrawerBackground = "#1a1a2e",
            DrawerText = "#e0e0e0",
            DrawerIcon = "#9B85FF",
        },
        PaletteDark = new PaletteDark
        {
            Primary = "#9B85FF",
            PrimaryDarken = "#7B61FF",
            PrimaryLighten = "#BBA8FF",
            Secondary = "#34d399",
            SecondaryDarken = "#10b981",
            SecondaryLighten = "#6ee7b7",
            AppbarBackground = "#0f0f1e",
            AppbarText = "#e0e0e0",
            Background = "#121212",
            Surface = "#1e1e2e",
            DrawerBackground = "#0f0f1e",
            DrawerText = "#c0c0c0",
            DrawerIcon = "#9B85FF",
        },
        Typography = new Typography
        {
            Default = new Default
            {
                FontFamily = new[] { "Inter", "Segoe UI", "Roboto", "Helvetica Neue", "Arial", "sans-serif" }
            }
        }
    };
}
```

**Theme registration approach**:
- If ELSA Studio 3.5.3 exposes an `IThemeProvider` or `IAppBarTheme`, implement and register it.
- If not, the theme colors are applied via MudBlazor's `MudThemeProvider` in a custom layout, or via CSS overrides in `tamma-overrides.css`.
- The `TammaThemeProvider` is a static class for now. Upgrade to a DI-registered service if ELSA Studio has a theme injection point.

### Step 9: Create CSS Overrides

**File**: `apps/tamma-elsa/src/Tamma.Studio/wwwroot/css/tamma-overrides.css`

```css
/* =============================================================================
   Tamma Studio — CSS Overrides
   Applied AFTER MudBlazor and ELSA Studio shell styles.
   ============================================================================= */

/* --- Workflow Designer Canvas --- */
.workflow-canvas,
.x6-graph {
    background-color: #f5f3ff !important; /* very light purple tint */
}

/* Dark mode canvas */
.mud-theme-dark .workflow-canvas,
.mud-theme-dark .x6-graph {
    background-color: #1a1a2e !important;
}

/* --- Activity Node Headers --- */
.activity-node .node-header,
.x6-node .node-header {
    background-color: #7B61FF !important;
    color: #ffffff !important;
}

/* --- Sidebar / Drawer --- */
.mud-drawer {
    border-right: 2px solid #7B61FF;
}

/* --- Loading spinner color --- */
.mud-progress-circular svg circle {
    stroke: #7B61FF;
}

/* --- Login page branding --- */
.login-page .mud-paper {
    border-top: 4px solid #7B61FF;
}

/* --- Scrollbar styling (webkit) --- */
::-webkit-scrollbar-thumb {
    background-color: #7B61FF40;
    border-radius: 4px;
}
::-webkit-scrollbar-thumb:hover {
    background-color: #7B61FF80;
}

/* --- Tamma logo sizing in nav --- */
.mud-drawer img.brand-logo {
    max-height: 36px;
    width: auto;
}
```

### Step 10: Create Static Assets

#### logo.svg

**File**: `apps/tamma-elsa/src/Tamma.Studio/wwwroot/logo.svg`

```svg
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 48" fill="none">
  <rect x="0" y="4" width="40" height="40" rx="8" fill="#7B61FF"/>
  <text x="8" y="34" font-family="Inter, Arial, sans-serif" font-weight="700" font-size="24" fill="#ffffff">T</text>
  <text x="48" y="36" font-family="Inter, Arial, sans-serif" font-weight="600" font-size="28" fill="#7B61FF">Tamma</text>
</svg>
```

This is a placeholder SVG logo. Replace with the official Tamma logo when available. The key requirements: purple `#7B61FF` branding, legible at 36px height in the sidebar.

#### favicon.ico

**File**: `apps/tamma-elsa/src/Tamma.Studio/wwwroot/favicon.ico`

For the MVP, generate a simple 16x16 and 32x32 ICO file with a purple "T" on white background. Tools:
- Use `convert` (ImageMagick): `convert -size 32x32 xc:#7B61FF -fill white -gravity center -pointsize 22 -annotate 0 "T" favicon.ico`
- Or use https://favicon.io/favicon-generator/ with text "T", background #7B61FF, font color white.
- Or create a 1x1 pixel placeholder and replace later.

For now, create an empty file and add the real favicon during development:
```bash
touch apps/tamma-elsa/src/Tamma.Studio/wwwroot/favicon.ico
```

### Step 11: Add Project to Solution File

**File to modify**: `apps/tamma-elsa/Tamma.sln`

Add the Tamma.Studio project entry. The GUID must be unique.

**Add this block** after the last `EndProject` line and before the `Global` line:

```
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Tamma.Studio", "src\Tamma.Studio\Tamma.Studio.csproj", "{2B3C4D5E-6F7A-8B9C-0D1E-F2A3B4C5D6E7}"
EndProject
```

**Add build configurations** inside `GlobalSection(ProjectConfigurationPlatforms)`:

```
{2B3C4D5E-6F7A-8B9C-0D1E-F2A3B4C5D6E7}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
{2B3C4D5E-6F7A-8B9C-0D1E-F2A3B4C5D6E7}.Debug|Any CPU.Build.0 = Debug|Any CPU
{2B3C4D5E-6F7A-8B9C-0D1E-F2A3B4C5D6E7}.Release|Any CPU.ActiveCfg = Release|Any CPU
{2B3C4D5E-6F7A-8B9C-0D1E-F2A3B4C5D6E7}.Release|Any CPU.Build.0 = Release|Any CPU
```

**Alternative** (recommended): Use the `dotnet sln` CLI instead of manual editing:

```bash
cd apps/tamma-elsa
dotnet sln Tamma.sln add src/Tamma.Studio/Tamma.Studio.csproj
```

### Step 12: Verify NuGet Restore

```bash
cd apps/tamma-elsa/src/Tamma.Studio
dotnet restore
```

**Expected outcome**: All packages resolve. Zero warnings about version conflicts.

**If `Elsa.Studio.Login.BlazorWasm` is not found**: Try these alternatives in order:
1. `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm`
2. `Elsa.Studio.Login.HttpMessageHandler`
3. Remove the login package and test — ELSA Studio may include login UI via `Elsa.Studio.Shell`.

**If MudBlazor version conflict occurs**: Remove any explicit MudBlazor `<PackageReference>` and let it resolve transitively.

### Step 13: Verify Build

```bash
cd apps/tamma-elsa/src/Tamma.Studio
dotnet build -c Release
```

**Expected**: Build succeeds with 0 errors. Warnings about missing `IBrandingProvider` namespace are expected until the correct using directive is added.

### Step 14: Verify Publish Output

```bash
cd apps/tamma-elsa/src/Tamma.Studio
dotnet publish -c Release -o ./publish-test
ls -la publish-test/wwwroot/
ls -la publish-test/wwwroot/_framework/
```

**Expected**: `wwwroot/` contains `index.html`, `appsettings.json`, `logo.svg`, `favicon.ico`, `css/tamma-overrides.css`. `_framework/` contains `blazor.boot.json` and `.dll` files.

### Step 15: Local Smoke Test

```bash
# Ensure ELSA Server is running on port 5000
cd apps/tamma-elsa/src/Tamma.Studio
# Update appsettings.json temporarily for local dev
sed -i 's|http://localhost:13000|http://localhost:5000|' wwwroot/appsettings.json
dotnet run
# Open browser to https://localhost:5280 (or whatever port dotnet assigns)
```

**Verify**:
- [ ] Browser shows "Tamma Studio" in the tab title
- [ ] Tamma logo appears in the sidebar
- [ ] Purple primary color is visible in UI elements
- [ ] Login page appears (ELSA Identity)
- [ ] After login, workflow list loads from the ELSA Server
- [ ] Dark mode toggle works and shows correct dark palette

**Revert**: `git checkout wwwroot/appsettings.json`

---

## Files to Create

| # | Path | Description |
|---|------|-------------|
| 1 | `apps/tamma-elsa/src/Tamma.Studio/Tamma.Studio.csproj` | Blazor WASM project file |
| 2 | `apps/tamma-elsa/src/Tamma.Studio/Program.cs` | WASM host builder with ELSA Studio registration |
| 3 | `apps/tamma-elsa/src/Tamma.Studio/App.razor` | Root Blazor component |
| 4 | `apps/tamma-elsa/src/Tamma.Studio/_Imports.razor` | Global using directives |
| 5 | `apps/tamma-elsa/src/Tamma.Studio/wwwroot/index.html` | HTML host page |
| 6 | `apps/tamma-elsa/src/Tamma.Studio/wwwroot/appsettings.json` | ELSA Server URL config |
| 7 | `apps/tamma-elsa/src/Tamma.Studio/wwwroot/css/tamma-overrides.css` | Theme CSS overrides |
| 8 | `apps/tamma-elsa/src/Tamma.Studio/wwwroot/logo.svg` | Tamma logo SVG |
| 9 | `apps/tamma-elsa/src/Tamma.Studio/wwwroot/favicon.ico` | Browser favicon |
| 10 | `apps/tamma-elsa/src/Tamma.Studio/Branding/TammaBrandingProvider.cs` | IBrandingProvider impl |
| 11 | `apps/tamma-elsa/src/Tamma.Studio/Theming/TammaThemeProvider.cs` | MudTheme definition |

## Files to Modify

| # | Path | Change |
|---|------|--------|
| 1 | `apps/tamma-elsa/Tamma.sln` | Add Tamma.Studio project reference |

---

## Risks and Edge Cases

### 1. NuGet Package Name Uncertainty

ELSA Studio package naming has changed between versions. The story references `Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm` but the actual 3.5.3 package may be `Elsa.Studio.Login.BlazorWasm`. Run `dotnet restore` early and inspect errors.

**Mitigation**: Check NuGet.org for `Elsa.Studio` packages at version 3.5.3 before writing code.

### 2. IBrandingProvider Interface Location

The exact namespace for `IBrandingProvider` is not documented in the story. It may be in:
- `Elsa.Studio.Contracts`
- `Elsa.Studio.Core.Contracts`
- `Elsa.Studio.Models`

**Mitigation**: After `dotnet restore`, inspect the restored DLL with reflection or the ELSA Studio source.

### 3. ELSA Studio Shell Component Name

The story assumes `ElsaStudioShell` exists as a Blazor component. If ELSA Studio 3.5.3 uses a different component name (e.g., `ElsaStudioApp`, `ShellLayout`), the `App.razor` must be adjusted.

**Mitigation**: Check `_content/Elsa.Studio.Shell/` in the publish output for component registration.

### 4. MudBlazor CSS/JS Path

The `index.html` references `_content/MudBlazor/MudBlazor.min.css`. If MudBlazor is embedded differently in ELSA Studio's dependencies, the path may differ.

**Mitigation**: After publish, inspect `wwwroot/_content/` for actual paths.

### 5. WASM Size

Blazor WASM downloads 15-30MB of .NET assemblies on first load. Without compression (handled in Story 14.2), this is slow.

**Mitigation**: Acceptable for MVP. Story 14.2 adds gzip in nginx. IL trimming can be added later via `<PublishTrimmed>true</PublishTrimmed>` (risky with reflection-heavy ELSA).

### 6. Authentication Flow

ELSA Studio authenticates against the ELSA Server's identity endpoint. The `AddLoginModule()` call must match the ELSA Server's identity configuration. The server uses `UseIdentity()` with `UseAdminUserProvider()`.

**Mitigation**: Verify login works end-to-end in Step 15. If login fails, check CORS settings on the ELSA Server (the Studio's origin must be allowed).

---

## Verification Checklist

- [ ] `dotnet restore` succeeds with zero version conflicts
- [ ] `dotnet build -c Release` succeeds with zero errors
- [ ] `dotnet publish -c Release` produces expected wwwroot output
- [ ] `Tamma.sln` includes Tamma.Studio project
- [ ] `dotnet build Tamma.sln` builds all projects including Tamma.Studio
- [ ] Local `dotnet run` shows Tamma branding (title, logo, colors)
- [ ] Studio connects to ELSA Server and loads workflow definitions
- [ ] Dark mode toggle shows correct dark palette
- [ ] No JavaScript console errors in browser devtools
