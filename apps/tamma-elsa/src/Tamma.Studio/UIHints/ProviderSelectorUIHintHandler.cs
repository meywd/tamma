using Elsa.Studio.Contracts;
using Elsa.Studio.Models;
using Microsoft.AspNetCore.Components;
using Tamma.Studio.Components;

namespace Tamma.Studio.UIHints;

/// <summary>
/// Studio-side UI hint handler for the "tamma-provider-selector" hint.
/// Renders a multi-select dropdown of known LLM provider names.
///
/// See <see cref="JsonEditorUIHintHandler"/> for notes on the ELSA Studio extensibility pattern.
/// </summary>
public class ProviderSelectorUIHintHandler : IUIHintHandler
{
    /// <inheritdoc />
    public string UISyntax => "Literal";

    /// <inheritdoc />
    public bool GetSupportsUIHint(string uiHint)
    {
        return string.Equals(uiHint, "tamma-provider-selector", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public RenderFragment DisplayInputEditor(DisplayInputEditorContext context)
    {
        return builder =>
        {
            builder.OpenComponent<ProviderSelector>(0);
            builder.AddAttribute(1, nameof(ProviderSelector.Value),
                context.Value?.ToString() ?? string.Empty);
            builder.AddAttribute(2, nameof(ProviderSelector.ValueChanged),
                EventCallback.Factory.Create<string>(
                    this,
                    async value => await context.UpdateValueOrLiteralExpressionAsync(value)));
            builder.AddAttribute(3, nameof(ProviderSelector.IsReadOnly), context.IsReadOnly);
            builder.CloseComponent();
        };
    }
}
