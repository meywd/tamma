using Elsa.Studio.Contracts;
using Elsa.Studio.Models;
using Microsoft.AspNetCore.Components;
using Tamma.Studio.Components;

namespace Tamma.Studio.UIHints;

/// <summary>
/// Studio-side UI hint handler for the "json-editor" hint.
/// Renders the Tamma <see cref="JsonEditor"/> Blazor component with validation and
/// formatting instead of the default text input.
///
/// Registered via <c>services.AddUIHintHandler&lt;JsonEditorUIHintHandler&gt;()</c> in Program.cs.
/// </summary>
public class JsonEditorUIHintHandler : IUIHintHandler
{
    /// <inheritdoc />
    public string UISyntax => "Json";

    /// <inheritdoc />
    public bool GetSupportsUIHint(string uiHint)
    {
        return string.Equals(uiHint, "json-editor", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public RenderFragment DisplayInputEditor(DisplayInputEditorContext context)
    {
        return builder =>
        {
            builder.OpenComponent<JsonEditor>(0);
            builder.AddAttribute(1, nameof(JsonEditor.Value),
                context.Value?.ToString() ?? "{}");
            builder.AddAttribute(2, nameof(JsonEditor.ValueChanged),
                EventCallback.Factory.Create<string>(
                    this,
                    async value => await context.UpdateValueOrLiteralExpressionAsync(value)));
            builder.AddAttribute(3, nameof(JsonEditor.IsReadOnly), context.IsReadOnly);
            builder.CloseComponent();
        };
    }
}
