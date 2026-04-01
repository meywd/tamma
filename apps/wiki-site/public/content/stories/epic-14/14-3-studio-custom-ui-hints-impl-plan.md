---
title: "Story 14.3: Studio Custom UI Hints — Implementation Plan"
sidebar:
  order: 140
---

## Overview

Add custom UI hint handlers to Tamma Studio for JSON editing and provider selection, plus custom navigation menu items. Annotate existing activity input properties with UIHint attributes so the Studio renders specialized editors instead of plain text fields.

**Dependencies**: Story 14.1 must be complete (Tamma.Studio project exists and builds).

**Two projects are modified**:
1. `Tamma.Studio` (Blazor WASM) -- new UI hint handlers, components, menu provider
2. `Tamma.Activities` (.NET class library) -- UIHint attributes on activity input properties

---

## Step-by-Step Implementation Tasks

### Step 1: Create the JSON Editor Blazor Component

This is the visual component rendered when the `"tamma-json-editor"` UI hint is triggered. MVP uses a `<textarea>` with JSON validation and formatting. A full Monaco Editor can replace this later.

**File**: `apps/tamma-elsa/src/Tamma.Studio/Components/JsonEditor.razor`

```razor
@using System.Text.Json

<div class="tamma-json-editor">
    <MudTextField @bind-Value="_text"
                  Variant="Variant.Outlined"
                  Lines="12"
                  FullWidth="true"
                  Placeholder="{}"
                  Error="@_hasError"
                  ErrorText="@_errorText"
                  Immediate="false"
                  DebounceInterval="500"
                  OnDebounceIntervalElapsed="OnTextChanged"
                  Class="json-editor-field"
                  Style="font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace; font-size: 13px;" />

    <div class="json-editor-toolbar" style="margin-top: 4px; display: flex; gap: 8px;">
        <MudButton Variant="Variant.Text"
                   Size="Size.Small"
                   StartIcon="@Icons.Material.Filled.FormatAlignLeft"
                   OnClick="FormatJson"
                   Disabled="@_hasError">
            Format
        </MudButton>
        <MudButton Variant="Variant.Text"
                   Size="Size.Small"
                   StartIcon="@Icons.Material.Filled.Compress"
                   OnClick="MinifyJson">
            Minify
        </MudButton>
        @if (_hasError)
        {
            <MudText Typo="Typo.caption" Color="Color.Error" Class="mt-1">@_errorText</MudText>
        }
        else
        {
            <MudText Typo="Typo.caption" Color="Color.Success" Class="mt-1">Valid JSON</MudText>
        }
    </div>
</div>

@code {
    private string _text = "{}";
    private bool _hasError;
    private string _errorText = string.Empty;

    [Parameter]
    public string Value { get; set; } = "{}";

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    protected override void OnParametersSet()
    {
        if (Value != _text)
        {
            _text = Value ?? "{}";
            ValidateJson();
        }
    }

    private async Task OnTextChanged(string newValue)
    {
        _text = newValue;
        ValidateJson();
        if (!_hasError)
        {
            await ValueChanged.InvokeAsync(_text);
        }
    }

    private void ValidateJson()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_text))
            {
                _hasError = false;
                _errorText = string.Empty;
                return;
            }
            JsonDocument.Parse(_text);
            _hasError = false;
            _errorText = string.Empty;
        }
        catch (JsonException ex)
        {
            _hasError = true;
            _errorText = $"JSON error: {ex.Message}";
        }
    }

    private async Task FormatJson()
    {
        try
        {
            var doc = JsonDocument.Parse(_text);
            _text = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            _hasError = false;
            _errorText = string.Empty;
            await ValueChanged.InvokeAsync(_text);
        }
        catch (JsonException)
        {
            // Leave as-is if invalid
        }
    }

    private async Task MinifyJson()
    {
        try
        {
            var doc = JsonDocument.Parse(_text);
            _text = JsonSerializer.Serialize(doc);
            _hasError = false;
            _errorText = string.Empty;
            await ValueChanged.InvokeAsync(_text);
        }
        catch (JsonException)
        {
            // Leave as-is if invalid
        }
    }
}
```

### Step 2: Create the Provider Selector Blazor Component

A multi-select dropdown listing known AI provider names.

**File**: `apps/tamma-elsa/src/Tamma.Studio/Components/ProviderSelector.razor`

```razor
<div class="tamma-provider-selector">
    <MudSelect T="string"
               MultiSelection="true"
               @bind-SelectedValues="_selectedProviders"
               Label="LLM Providers"
               Variant="Variant.Outlined"
               AnchorOrigin="Origin.BottomCenter"
               FullWidth="true"
               Clearable="true"
               SelectedValuesChanged="OnSelectionChanged">
        @foreach (var provider in _availableProviders)
        {
            <MudSelectItem T="string" Value="@provider.Key">
                <div style="display:flex; align-items:center; gap:8px;">
                    <MudIcon Icon="@Icons.Material.Filled.SmartToy" Size="Size.Small" />
                    <span>@provider.Value</span>
                </div>
            </MudSelectItem>
        }
    </MudSelect>
</div>

@code {
    private IEnumerable<string> _selectedProviders = new List<string>();

    /// <summary>
    /// Comma-separated list of selected provider keys.
    /// </summary>
    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    /// <summary>
    /// Known AI provider identifiers and display names.
    /// Matches the provider keys used in Tamma's IAIProvider registry.
    /// </summary>
    private static readonly Dictionary<string, string> _availableProviders = new()
    {
        { "anthropic", "Anthropic Claude" },
        { "openai", "OpenAI" },
        { "openrouter", "OpenRouter" },
        { "google", "Google Gemini" },
        { "github-copilot", "GitHub Copilot" },
        { "local-llm", "Local LLM" },
        { "opencode", "OpenCode" },
        { "z-ai", "z.ai" },
        { "zen-mcp", "Zen MCP" },
    };

    protected override void OnParametersSet()
    {
        if (!string.IsNullOrEmpty(Value))
        {
            _selectedProviders = Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => _availableProviders.ContainsKey(v))
                .ToList();
        }
    }

    private async Task OnSelectionChanged(IEnumerable<string> selectedValues)
    {
        _selectedProviders = selectedValues;
        var csv = string.Join(",", _selectedProviders);
        await ValueChanged.InvokeAsync(csv);
    }
}
```

### Step 3: Create the JSON Editor UI Hint Handler

This class connects the `"tamma-json-editor"` hint name to the `JsonEditor` Blazor component.

**File**: `apps/tamma-elsa/src/Tamma.Studio/UIHints/JsonEditorUIHintHandler.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tamma.Studio.Components;

namespace Tamma.Studio.UIHints;

/// <summary>
/// UI hint handler for the "tamma-json-editor" hint.
/// Renders a JSON editor with syntax validation and formatting for activity inputs
/// like ToolsJson on CallLlmActivity and PlanJson on WaitForPlanApprovalActivity.
///
/// IMPORTANT: The exact interface to implement depends on the ELSA Studio 3.5.3 API.
/// ELSA Studio uses IUIHintHandler or IInputDisplayComponentProvider.
/// Verify the correct interface after NuGet restore.
///
/// Option A — IUIHintHandler (ELSA Studio 3.x pattern):
/// </summary>
public class JsonEditorUIHintHandler : IUIHintHandler
{
    public string UIHint => "tamma-json-editor";

    public RenderFragment DisplayInputEditor(DisplayInputEditorContext context)
    {
        return builder =>
        {
            builder.OpenComponent<JsonEditor>(0);
            builder.AddAttribute(1, nameof(JsonEditor.Value),
                context.Value?.ToString() ?? "{}");
            builder.AddAttribute(2, nameof(JsonEditor.ValueChanged),
                EventCallback.Factory.Create<string>(
                    context.Owner,
                    value => context.OnValueChanged(value)));
            builder.CloseComponent();
        };
    }
}
```

**CRITICAL NOTE on ELSA Studio 3.5.3 API**:

The `IUIHintHandler` interface, `DisplayInputEditorContext`, and registration pattern may differ from the code above. The ELSA Studio extensibility API has evolved across versions. After NuGet restore, verify:

1. Search for `IUIHintHandler` in the restored ELSA Studio assemblies.
2. If it does not exist, look for:
   - `IInputDisplayComponentProvider`
   - `IUIHintDisplayDriver`
   - `IActivityInputUIHintHandler`
3. Check the ELSA Studio GitHub repository at tag `v3.5.3` for the current extensibility pattern.
4. The `DisplayInputEditorContext` type may be named differently (e.g., `InputEditorContext`, `UIHintContext`).

**Fallback approach if IUIHintHandler does not exist**:

Use the `[InputPropertyUIHint]` attribute-based approach where you register a component directly:

```csharp
// Register in Program.cs:
builder.Services.AddScoped<IUIHintHandler, JsonEditorUIHintHandler>();
// or
builder.Services.Configure<UIHintOptions>(options =>
{
    options.Register("tamma-json-editor", typeof(JsonEditor));
});
```

### Step 4: Create the Provider Selector UI Hint Handler

**File**: `apps/tamma-elsa/src/Tamma.Studio/UIHints/ProviderSelectorUIHintHandler.cs`

```csharp
using Microsoft.AspNetCore.Components;
using Tamma.Studio.Components;

namespace Tamma.Studio.UIHints;

/// <summary>
/// UI hint handler for the "tamma-provider-selector" hint.
/// Renders a multi-select dropdown for choosing LLM providers.
///
/// See JsonEditorUIHintHandler.cs for notes on ELSA Studio API compatibility.
/// </summary>
public class ProviderSelectorUIHintHandler : IUIHintHandler
{
    public string UIHint => "tamma-provider-selector";

    public RenderFragment DisplayInputEditor(DisplayInputEditorContext context)
    {
        return builder =>
        {
            builder.OpenComponent<ProviderSelector>(0);
            builder.AddAttribute(1, nameof(ProviderSelector.Value),
                context.Value?.ToString() ?? string.Empty);
            builder.AddAttribute(2, nameof(ProviderSelector.ValueChanged),
                EventCallback.Factory.Create<string>(
                    context.Owner,
                    value => context.OnValueChanged(value)));
            builder.CloseComponent();
        };
    }
}
```

### Step 5: Create the Custom Menu Provider

**File**: `apps/tamma-elsa/src/Tamma.Studio/Navigation/TammaMenuProvider.cs`

```csharp
namespace Tamma.Studio.Navigation;

/// <summary>
/// Adds Tamma-specific menu items to the ELSA Studio navigation sidebar.
/// Links to filtered workflow instance views for ADL, LLM, and Mentorship workflows.
///
/// IMPORTANT: The exact interface depends on ELSA Studio 3.5.3 API.
/// Common patterns:
///   - IMenuProvider (returns MenuItems)
///   - IMenuContributor (contributes to a MenuBuilder)
///   - INavigationProvider
///
/// Verify after NuGet restore.
/// </summary>
public class TammaMenuProvider : IMenuProvider
{
    public ValueTask<IEnumerable<MenuItem>> GetMenuItemsAsync(
        CancellationToken cancellationToken = default)
    {
        var items = new List<MenuItem>
        {
            new()
            {
                Text = "ADL Dashboard",
                Href = "/workflow-instances?definitionId=adl-orchestrator",
                Icon = "Dashboard",
                Order = 100,
            },
            new()
            {
                Text = "LLM Diagnostics",
                Href = "/workflow-instances?definitionId=llm-call",
                Icon = "Analytics",
                Order = 101,
            },
            new()
            {
                Text = "Mentorship",
                Href = "/workflow-instances?definitionId=mentorship",
                Icon = "School",
                Order = 102,
            },
        };

        return ValueTask.FromResult<IEnumerable<MenuItem>>(items);
    }
}
```

**IMPORTANT**: The `IMenuProvider` interface, `MenuItem` class, and their properties (`Text`, `Href`, `Icon`, `Order`) must be verified against the ELSA Studio 3.5.3 API. The actual interface may use different names:
- `IMenuProvider` with `MenuItem { Text, Url, Icon, SortOrder }`
- `INavigationProvider` with `NavigationItem { Label, Path, IconName }`
- A different pattern entirely

After NuGet restore, search the ELSA Studio assemblies for menu-related interfaces:
```bash
# Search restored NuGet packages for menu interfaces
find ~/.nuget/packages/elsa.studio* -name "*.dll" -exec strings {} \; | grep -i "IMenu\|INavigation\|MenuItem"
```

### Step 6: Register Custom Providers in Program.cs

**File to modify**: `apps/tamma-elsa/src/Tamma.Studio/Program.cs`

Add the following registrations after the existing service registrations:

```csharp
using Tamma.Studio.Branding;
using Tamma.Studio.Navigation;
using Tamma.Studio.UIHints;

// ... (after AddWorkflowsModule())

// Tamma branding (already added in Story 14.1)
builder.Services.AddScoped<IBrandingProvider, TammaBrandingProvider>();

// Tamma custom menu items
builder.Services.AddScoped<IMenuProvider, TammaMenuProvider>();

// Tamma custom UI hint handlers
builder.Services.AddScoped<IUIHintHandler, JsonEditorUIHintHandler>();
builder.Services.AddScoped<IUIHintHandler, ProviderSelectorUIHintHandler>();
```

**Full updated Program.cs**:

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
using Tamma.Studio.Navigation;
using Tamma.Studio.UIHints;

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

// Tamma custom navigation menu items
builder.Services.AddScoped<IMenuProvider, TammaMenuProvider>();

// Tamma custom UI hint handlers
builder.Services.AddScoped<IUIHintHandler, JsonEditorUIHintHandler>();
builder.Services.AddScoped<IUIHintHandler, ProviderSelectorUIHintHandler>();

await builder.Build().RunAsync();
```

**Note on DI registration lifetime**:
- ELSA Studio components run in Blazor WASM (single user, single circuit). `AddScoped` and `AddSingleton` behave identically in WASM. Use `AddScoped` for consistency with ELSA Studio's own registrations.
- If ELSA Studio uses a specific registration method (e.g., `builder.Services.AddUIHintHandler<JsonEditorUIHintHandler>()`), use that instead of raw `AddScoped`.

### Step 7: Add UIHint Attribute to CallLlmActivity.ToolsJson

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs`

**Current** (line 65-66):
```csharp
    /// <summary>Serialized tools JSON (list of ResolvedTool).</summary>
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)")]
    public Input<string?> ToolsJson { get; set; } = default!;
```

**Change to**:
```csharp
    /// <summary>Serialized tools JSON (list of ResolvedTool).</summary>
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)", UIHint = "tamma-json-editor")]
    public Input<string?> ToolsJson { get; set; } = default!;
```

The `UIHint` property is part of ELSA's `[Input]` attribute. Adding `UIHint = "tamma-json-editor"` tells ELSA Studio to use the registered `JsonEditorUIHintHandler` instead of the default text input.

### Step 8: Add UIHint Attribute to WaitForPlanApprovalActivity.PlanJson

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForPlanApprovalActivity.cs`

**Current** (line 39):
```csharp
    [Input(Description = "Generated plan JSON to present for approval")]
    public Input<string> PlanJson { get; set; } = default!;
```

**Change to**:
```csharp
    [Input(Description = "Generated plan JSON to present for approval", UIHint = "tamma-json-editor")]
    public Input<string> PlanJson { get; set; } = default!;
```

### Step 9: Add UIHint Attribute to ResolveLlmPromptActivity.SystemPromptOverride

**File to modify**: `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveLlmPromptActivity.cs`

**Current** (line 53-54):
```csharp
    /// <summary>Optional caller-provided system prompt override.</summary>
    [Input(Description = "Explicit system prompt override (optional)")]
    public Input<string?> SystemPromptOverride { get; set; } = default!;
```

**Change to**:
```csharp
    /// <summary>Optional caller-provided system prompt override.</summary>
    [Input(Description = "Explicit system prompt override (optional)", UIHint = "multi-line")]
    public Input<string?> SystemPromptOverride { get; set; } = default!;
```

Note: `"multi-line"` is a standard ELSA UI hint (built-in), not a custom one. It renders a multi-line textarea instead of a single-line text input.

### Step 10: Update _Imports.razor

**File to modify**: `apps/tamma-elsa/src/Tamma.Studio/_Imports.razor`

Add component namespaces:

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
@using Tamma.Studio.Components
@using Tamma.Studio.UIHints
@using Tamma.Studio.Navigation
```

### Step 11: Create UIHint Attribute Reflection Tests

Automated tests that verify the UIHint attributes are correctly applied to activity properties. These run as part of the existing `Tamma.Activities.Tests` project.

**File**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/UIHint/UIHintAttributeTests.cs`

```csharp
using System.Reflection;
using Elsa.Workflows.Attributes;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Tests.UIHint;

/// <summary>
/// Verifies that activity input properties have the expected UIHint attributes.
/// These attributes control which editor component ELSA Studio renders.
/// </summary>
[TestFixture]
public class UIHintAttributeTests
{
    [Test]
    public void CallLlmActivity_ToolsJson_HasJsonEditorUIHint()
    {
        var property = typeof(CallLlmActivity).GetProperty("ToolsJson");
        property.Should().NotBeNull("CallLlmActivity must have a ToolsJson property");

        var inputAttr = property!.GetCustomAttribute<InputAttribute>();
        inputAttr.Should().NotBeNull("ToolsJson must have an [Input] attribute");
        inputAttr!.UIHint.Should().Be("tamma-json-editor",
            "ToolsJson should use the tamma-json-editor UI hint for JSON editing");
    }

    [Test]
    public void WaitForPlanApprovalActivity_PlanJson_HasJsonEditorUIHint()
    {
        var property = typeof(WaitForPlanApprovalActivity).GetProperty("PlanJson");
        property.Should().NotBeNull("WaitForPlanApprovalActivity must have a PlanJson property");

        var inputAttr = property!.GetCustomAttribute<InputAttribute>();
        inputAttr.Should().NotBeNull("PlanJson must have an [Input] attribute");
        inputAttr!.UIHint.Should().Be("tamma-json-editor",
            "PlanJson should use the tamma-json-editor UI hint for JSON editing");
    }

    [Test]
    public void ResolveLlmPromptActivity_SystemPromptOverride_HasMultiLineUIHint()
    {
        var property = typeof(ResolveLlmPromptActivity).GetProperty("SystemPromptOverride");
        property.Should().NotBeNull("ResolveLlmPromptActivity must have a SystemPromptOverride property");

        var inputAttr = property!.GetCustomAttribute<InputAttribute>();
        inputAttr.Should().NotBeNull("SystemPromptOverride must have an [Input] attribute");
        inputAttr!.UIHint.Should().Be("multi-line",
            "SystemPromptOverride should use the multi-line UI hint for long text");
    }

    [Test]
    public void CallLlmActivity_NonJsonInputs_DoNotHaveJsonEditorHint()
    {
        // Verify other inputs on CallLlmActivity do NOT accidentally get the JSON editor hint
        var nonJsonProperties = new[] { "ProviderName", "SystemPrompt", "UserPrompt", "ModelOverride", "MaxTokens", "Temperature", "AttemptNumber" };

        foreach (var propName in nonJsonProperties)
        {
            var property = typeof(CallLlmActivity).GetProperty(propName);
            if (property == null) continue;

            var inputAttr = property.GetCustomAttribute<InputAttribute>();
            if (inputAttr?.UIHint != null)
            {
                inputAttr.UIHint.Should().NotBe("tamma-json-editor",
                    $"{propName} should not use the JSON editor hint");
            }
        }
    }
}
```

**Verify**: The `InputAttribute` in ELSA has a `UIHint` property. Check after NuGet restore:
```bash
# Verify InputAttribute has UIHint property
dotnet build apps/tamma-elsa/tests/Tamma.Activities.Tests
```

If `InputAttribute` does not have a `UIHint` property in ELSA 3.5.3, the UIHint may be set differently:
- Separate `[UIHint("tamma-json-editor")]` attribute alongside `[Input(...)]`
- `[Input(... , UIHint = "tamma-json-editor")]` where UIHint is named differently

Adjust the test reflection code accordingly.

### Step 12: Verify Tamma.Activities Build

```bash
cd apps/tamma-elsa
dotnet build src/Tamma.Activities/Tamma.Activities.csproj
```

This must succeed after adding UIHint attributes. If `UIHint` is not a valid property on `[Input]`, the build will fail with a clear error.

### Step 13: Verify Tamma.Studio Build

```bash
cd apps/tamma-elsa
dotnet build src/Tamma.Studio/Tamma.Studio.csproj
```

This must succeed after adding UI hint handlers, components, and menu provider.

### Step 14: Run UIHint Attribute Tests

```bash
cd apps/tamma-elsa
dotnet test tests/Tamma.Activities.Tests --filter "FullyQualifiedName~UIHintAttributeTests"
```

All 4 tests must pass.

### Step 15: Full Solution Build

```bash
cd apps/tamma-elsa
dotnet build Tamma.sln
```

Ensures all projects (server, activities, studio, API, tests) build together without conflicts.

### Step 16: Manual Verification in Studio

After deploying (or running locally):

1. Open Tamma Studio in browser
2. Navigate to Workflow Definitions
3. Open or create a workflow with a `CallLlmActivity`
4. Click the `CallLlmActivity` node
5. Verify the `ToolsJson` input shows the JSON editor (textarea with Format/Minify buttons), NOT a plain text input
6. Enter `{"invalid json` -- verify red error border and "JSON error:" message
7. Enter `{"tools":[]}` -- verify green "Valid JSON" indicator
8. Click "Format" -- verify pretty-prints the JSON
9. Navigate to a `WaitForPlanApprovalActivity` node -- verify `PlanJson` also shows JSON editor
10. Navigate to a `ResolveLlmPromptActivity` node -- verify `SystemPromptOverride` shows a multi-line textarea (taller than single-line)
11. Check sidebar navigation for custom menu items: "ADL Dashboard", "LLM Diagnostics", "Mentorship"
12. Click each menu item -- verify it navigates to filtered workflow instances view

---

## Files to Create

| # | Path | Description |
|---|------|-------------|
| 1 | `apps/tamma-elsa/src/Tamma.Studio/Components/JsonEditor.razor` | JSON editor Blazor component with validation/formatting |
| 2 | `apps/tamma-elsa/src/Tamma.Studio/Components/ProviderSelector.razor` | Multi-select provider dropdown component |
| 3 | `apps/tamma-elsa/src/Tamma.Studio/UIHints/JsonEditorUIHintHandler.cs` | IUIHintHandler for "tamma-json-editor" |
| 4 | `apps/tamma-elsa/src/Tamma.Studio/UIHints/ProviderSelectorUIHintHandler.cs` | IUIHintHandler for "tamma-provider-selector" |
| 5 | `apps/tamma-elsa/src/Tamma.Studio/Navigation/TammaMenuProvider.cs` | IMenuProvider with Tamma-specific nav items |
| 6 | `apps/tamma-elsa/tests/Tamma.Activities.Tests/UIHint/UIHintAttributeTests.cs` | Reflection tests for UIHint attributes |

## Files to Modify

| # | Path | Change |
|---|------|--------|
| 1 | `apps/tamma-elsa/src/Tamma.Studio/Program.cs` | Register IMenuProvider, IUIHintHandler x2 in DI |
| 2 | `apps/tamma-elsa/src/Tamma.Studio/_Imports.razor` | Add component/UIHint/Navigation namespaces |
| 3 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` | Add `UIHint = "tamma-json-editor"` to ToolsJson [Input] |
| 4 | `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForPlanApprovalActivity.cs` | Add `UIHint = "tamma-json-editor"` to PlanJson [Input] |
| 5 | `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveLlmPromptActivity.cs` | Add `UIHint = "multi-line"` to SystemPromptOverride [Input] |

---

## Code Snippets: Exact Attribute Changes

### CallLlmActivity.cs (line ~65-66)

**Before**:
```csharp
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)")]
```

**After**:
```csharp
    [Input(Description = "Serialized tools (JSON array of ResolvedTool)", UIHint = "tamma-json-editor")]
```

### WaitForPlanApprovalActivity.cs (line ~39)

**Before**:
```csharp
    [Input(Description = "Generated plan JSON to present for approval")]
```

**After**:
```csharp
    [Input(Description = "Generated plan JSON to present for approval", UIHint = "tamma-json-editor")]
```

### ResolveLlmPromptActivity.cs (line ~53-54)

**Before**:
```csharp
    [Input(Description = "Explicit system prompt override (optional)")]
```

**After**:
```csharp
    [Input(Description = "Explicit system prompt override (optional)", UIHint = "multi-line")]
```

---

## Risks and Edge Cases

### 1. IUIHintHandler Interface Does Not Exist in ELSA Studio 3.5.3

The ELSA Studio extensibility API is not stable across minor versions. `IUIHintHandler` is the documented pattern, but may not exist in 3.5.3.

**Mitigation**: After NuGet restore, search for the interface:
```bash
find ~/.nuget/packages/elsa.studio* -name "*.dll" | xargs strings | grep -i "UIHintHandler\|InputDisplay\|InputEditor"
```

**Fallback approaches** (in order of preference):
1. Check ELSA Studio GitHub at tag `v3.5.3` for the current extensibility API
2. Use JavaScript interop to render a custom editor (bypasses Blazor component system)
3. Defer UI hints to a future version and only implement menu items + UIHint attributes (the attributes still work even without Studio-side handlers -- they just fall back to the default text input)

### 2. InputAttribute.UIHint Property Name Difference

The `[Input]` attribute in ELSA may use a different property name for UI hints:
- `UIHint` (expected)
- `InputUIHint`
- `EditorHint`
- A separate attribute: `[UIHint("...")]`

**Mitigation**: Inspect `InputAttribute` after NuGet restore:
```bash
dotnet metadata --assembly ~/.nuget/packages/elsa/3.5.3/lib/net8.0/Elsa.dll --type InputAttribute
```

### 3. Blazor WASM Component Rendering in ELSA Studio Context

The UI hint components (JsonEditor, ProviderSelector) are rendered within ELSA Studio's component tree. They must be compatible with ELSA Studio's RenderFragment pattern. If ELSA Studio wraps inputs in its own form context, the `@bind-Value` pattern may conflict.

**Mitigation**: Test the JSON editor in isolation first (add it to a test page in the Studio), then integrate via the UI hint handler.

### 4. MudBlazor Component Availability

The `MudTextField`, `MudSelect`, `MudButton`, `MudIcon`, `MudText` components used in the Blazor components come from MudBlazor. These must be available in the ELSA Studio's MudBlazor version (resolved transitively).

**Mitigation**: Do not pin MudBlazor explicitly. The components used are stable MudBlazor APIs available since v6.x.

### 5. UIHint Attributes Require Server Rebuild

Adding `UIHint` to `[Input]` attributes in `Tamma.Activities` affects the activity metadata served by the ELSA Server. Both the Server and Studio must be rebuilt and redeployed together.

**Mitigation**: Deploy Server and Studio simultaneously. The UIHint attributes are backwards-compatible -- if the Studio does not have a handler for a hint, it falls back to the default text input.

### 6. Menu Item URLs May Not Match Workflow Definition IDs

The menu items link to `/workflow-instances?definitionId=adl-orchestrator`. If the workflow definition IDs change, the links will show empty results.

**Mitigation**: Use the actual workflow definition IDs from the seeded workflow JSON files. Check `apps/tamma-elsa/workflows/*.json` for the `definitionId` values. Update menu item URLs accordingly.

### 7. JSON Editor Does Not Notify Parent on Invalid JSON

The current implementation only calls `ValueChanged` when JSON is valid. This means edits that make JSON temporarily invalid (during typing) do not save. The user must fix the JSON before navigating away.

**Mitigation**: This is acceptable MVP behavior. Alternative: emit on every keystroke with an `isValid` flag, but that requires changes to the UI hint handler contract.

---

## Testing Strategy

### Automated Tests

| Test | File | Assertion |
|------|------|-----------|
| UIHint on CallLlmActivity.ToolsJson | `UIHintAttributeTests.cs` | `UIHint == "tamma-json-editor"` |
| UIHint on WaitForPlanApprovalActivity.PlanJson | `UIHintAttributeTests.cs` | `UIHint == "tamma-json-editor"` |
| UIHint on ResolveLlmPromptActivity.SystemPromptOverride | `UIHintAttributeTests.cs` | `UIHint == "multi-line"` |
| Negative: non-JSON inputs do NOT have JSON editor hint | `UIHintAttributeTests.cs` | `UIHint != "tamma-json-editor"` |

### Manual Tests

| # | Test | Expected Result |
|---|------|-----------------|
| 1 | Open CallLlmActivity in Studio | ToolsJson shows JSON editor component |
| 2 | Enter invalid JSON in editor | Red border, error message displayed |
| 3 | Enter valid JSON, click Format | JSON is pretty-printed |
| 4 | Click Minify | JSON is compacted to single line |
| 5 | Open WaitForPlanApprovalActivity | PlanJson shows JSON editor component |
| 6 | Open ResolveLlmPromptActivity | SystemPromptOverride shows multi-line textarea |
| 7 | Check sidebar navigation | "ADL Dashboard", "LLM Diagnostics", "Mentorship" links appear |
| 8 | Click "ADL Dashboard" menu item | Navigates to workflow instances filtered by ADL |
| 9 | Provider selector renders | Multi-select dropdown with 9 provider options |
| 10 | Select multiple providers | Comma-separated value is stored |

---

## Verification Checklist

- [ ] `dotnet build Tamma.sln` succeeds (all projects)
- [ ] `dotnet test` UIHintAttributeTests: all 4 pass
- [ ] JSON editor component renders in Studio
- [ ] JSON validation works (valid = green, invalid = red)
- [ ] Format and Minify buttons work
- [ ] Provider selector renders with 9 options
- [ ] Multi-select produces comma-separated value
- [ ] Menu items appear in sidebar navigation
- [ ] Menu item links navigate to filtered views
- [ ] No JavaScript console errors in browser
- [ ] ELSA Server builds with UIHint attributes (no compile errors)
- [ ] Studio + Server deploy together without breaking existing workflows
