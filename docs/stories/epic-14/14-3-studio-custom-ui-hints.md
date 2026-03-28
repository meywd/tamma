# Story 14.3: Studio Custom UI Hints

Status: ready-for-dev

## Story

As a **workflow designer**,
I want custom UI hint handlers in Tamma Studio for JSON editing and provider selection,
so that workflow designers can edit JSON inputs (tool definitions, plan JSON) with a proper JSON editor instead of a plain text field, and select LLM providers from a multi-select dropdown instead of typing provider names.

## Acceptance Criteria

1. `JsonEditorUIHintHandler` implements the ELSA Studio UI hint handler for the hint name `"tamma-json-editor"`
2. The JSON editor provides syntax highlighting, validation, and formatting for JSON inputs
3. `ProviderSelectorUIHintHandler` implements the ELSA Studio UI hint handler for the hint name `"tamma-provider-selector"`
4. The provider selector renders as a multi-select dropdown with known provider names as options
5. `CallLlmActivity`'s `ToolsJson` input property is annotated with `UIHint("tamma-json-editor")`
6. `WaitForPlanApprovalActivity`'s `PlanJson` input property is annotated with `UIHint("tamma-json-editor")`
7. `ResolveLlmPromptActivity`'s `SystemPromptOverride` input property is annotated with `UIHint("multi-line")` (standard ELSA multi-line hint)
8. UI hints render correctly in ELSA Studio when editing activity properties
9. Custom menu items added: ADL Dashboard, LLM Diagnostics, Mentorship Sessions (linking to filtered workflow instance views)

## Technical Context

### ELSA Studio UI Hints

ELSA Studio uses a UI hint system to determine which editor to render for activity input properties. Custom hints are registered via `IUIHintHandler`:

```csharp
public class JsonEditorUIHintHandler : IUIHintHandler
{
    public string UIHint => "tamma-json-editor";
    public RenderFragment DisplayInputEditor(DisplayInputEditorContext context)
    {
        return builder =>
        {
            builder.OpenComponent<JsonEditor>(0);
            builder.AddAttribute(1, "Value", context.Value?.ToString() ?? "{}");
            builder.AddAttribute(2, "ValueChanged", EventCallback.Factory.Create<string>(this,
                value => context.OnValueChanged(value)));
            builder.CloseComponent();
        };
    }
}
```

### JSON Editor Component

The JSON editor should use a Blazor wrapper around a JavaScript JSON editor (e.g., Monaco Editor's JSON mode or a lighter alternative like `json-editor`). For MVP, a `<textarea>` with JSON validation and auto-formatting is acceptable.

### Provider Selector Component

The provider selector lists known provider names as checkboxes or a multi-select dropdown:

```
[ ] anthropic
[x] openai
[x] openrouter
[ ] google
[ ] github-copilot
[ ] local-llm
[ ] opencode
[ ] z-ai
[ ] zen-mcp
```

### Custom Menu Items

```csharp
public class TammaMenuProvider : IMenuProvider
{
    public ValueTask<IEnumerable<MenuItem>> GetMenuItemsAsync(CancellationToken ct)
    {
        return ValueTask.FromResult<IEnumerable<MenuItem>>(new[]
        {
            new MenuItem("ADL Dashboard", "/workflow-instances?definitionId=adl-orchestrator", "Dashboard"),
            new MenuItem("LLM Diagnostics", "/workflow-instances?definitionId=llm-call", "Analytics"),
            new MenuItem("Mentorship", "/workflow-instances?definitionId=mentorship", "School"),
        });
    }
}
```

### Files to Create

- `apps/tamma-elsa/src/Tamma.Studio/UIHints/JsonEditorUIHintHandler.cs`
- `apps/tamma-elsa/src/Tamma.Studio/UIHints/ProviderSelectorUIHintHandler.cs`
- `apps/tamma-elsa/src/Tamma.Studio/Components/JsonEditor.razor` (Blazor component wrapping a JSON editor)
- `apps/tamma-elsa/src/Tamma.Studio/Components/ProviderSelector.razor` (Blazor component for multi-select)
- `apps/tamma-elsa/src/Tamma.Studio/Navigation/TammaMenuProvider.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.Studio/Program.cs` — register UI hint handlers and menu provider in DI
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmActivity.cs` — add `[UIHint("tamma-json-editor")]` to `ToolsJson` property
- `apps/tamma-elsa/src/Tamma.Activities/ADL/WaitForPlanApprovalActivity.cs` — add `[UIHint("tamma-json-editor")]` to `PlanJson` property
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveLlmPromptActivity.cs` — add `[UIHint("multi-line")]` to `SystemPromptOverride` property

### UIHint Attribute

ELSA uses `[Input(UIHint = "tamma-json-editor")]` on activity input properties:

```csharp
public class CallLlmActivity : CodeActivity
{
    [Input(Description = "Tool definitions as JSON", UIHint = "tamma-json-editor")]
    public string? ToolsJson { get; set; }
}
```

The hint name must match the `UIHint` property on the handler class.

## Implementation Notes

1. **Start with the menu provider**: It is the simplest component and validates that the Studio extension system works. Register `IMenuProvider` in DI and verify menu items appear.
2. **JSON editor MVP**: For the first iteration, a `<textarea>` with a "Format JSON" button and validation feedback (red border on invalid JSON) is sufficient. A full Monaco Editor can be added later.
3. **Provider selector**: Use MudBlazor's `MudSelect` with `MultiSelection=true` for the provider dropdown. Provider names should come from a configuration class (not hardcoded in the component).
4. **UIHint attribute requires server rebuild**: Adding `[UIHint]` attributes to activity classes requires rebuilding the ELSA Server (not just the Studio). Activities are in `Tamma.Activities` which is referenced by `Tamma.ElsaServer`.
5. **Test in ELSA Studio**: After adding UI hints, open a workflow in the Studio editor, click on a `CallLlmActivity` node, and verify the JSON editor appears instead of a plain text field.
6. **Deferred features**: Custom activity tabs (LLM Call Diagnostics Tab, Workflow Lineage Tab) are deferred to a future story as they require deeper ELSA Studio customization.

## Testing Strategy

- **Build verification**: Both `Tamma.Studio` and `Tamma.Activities` build successfully with the UI hint attributes
- **Menu provider test**: Launch Studio, verify custom menu items appear in the navigation
- **JSON editor test**: Open a workflow with `CallLlmActivity`, verify the JSON editor renders for `ToolsJson`, enter invalid JSON and verify validation feedback
- **Provider selector test**: Verify the multi-select dropdown renders with known provider names
- **UIHint attribute test**: Verify `PlanJson` on `WaitForPlanApprovalActivity` uses the JSON editor, `SystemPromptOverride` uses multi-line text
- **No automated tests for UI hint components** (Blazor component testing requires bUnit; covered by manual inspection for MVP)
- **Attribute tests**: Unit test that activity classes have the expected `UIHint` attributes via reflection
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/UIHint/UIHintAttributeTests.cs`

## Dependencies

- **Story 14.1** (Studio Blazor WASM Scaffold) — the Studio project must exist before custom components can be added

## Estimated Effort

3-4 days

## Logging Requirements

### Existing Coverage

The story has **no logging requirements** specified. UI hint handlers and Blazor components run in the browser — same constraints as Story 14.1.

### Required Additions

UI hint handlers can use Blazor WASM's `ILogger<T>` for browser console logging.

| Event | Level | Structured Properties | Notes |
|-------|-------|----------------------|-------|
| JSON editor UI hint handler registered | DEBUG | `{HintName}` ("tamma-json-editor") | Logged during DI registration in `Program.cs` |
| Provider selector UI hint handler registered | DEBUG | `{HintName}` ("tamma-provider-selector") | Logged during DI registration in `Program.cs` |
| Menu provider registered | DEBUG | `{MenuItemCount}` | Logged during DI registration |
| JSON validation error in editor | WARN | `{FieldName}`, `{ErrorMessage}` | When user enters invalid JSON — browser console feedback for developers |
| Provider selection changed | DEBUG | `{SelectedProviders}`, `{FieldName}` | User interaction trace |

### Sensitive Data Redaction

- Do NOT log the JSON content from the editor — it may contain tool definitions with sensitive schema details.
- Provider names are from a known list and safe to log.

### Correlation IDs

- Not applicable for browser-side UI components. No workflow correlation needed.

### Note on Log Priority

This story has the **lowest logging priority** in the entire audit. It is a UI customization story. The 5 log statements above are for developer convenience during debugging, not for production observability.

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/elsa-studio-customization.md` Phases 5+6 | Architecture Team |
| 2026-03-28 | 1.1 | Added Logging Requirements section | Logging Audit |
