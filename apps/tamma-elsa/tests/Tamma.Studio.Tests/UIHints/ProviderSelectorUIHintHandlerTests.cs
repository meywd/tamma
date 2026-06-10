using Elsa.Studio.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using NUnit.Framework;
using Tamma.Studio.Components;
using Tamma.Studio.UIHints;

namespace Tamma.Studio.Tests.UIHints;

/// <summary>
/// Unit tests for <see cref="ProviderSelectorUIHintHandler"/>.
///
/// Like <c>JsonEditorUIHintHandler</c>, this is a thin Studio bridge that
/// claims the <c>tamma-provider-selector</c> hint and renders the
/// <see cref="ProviderSelector"/> Blazor component.
///
/// The set of available providers is exposed by the component itself via the
/// static <c>ProviderSelector.AvailableProviders</c> dictionary — there is
/// NO injected registry to mock.  The handler simply mounts the component
/// with the current value bound; the component reads its own provider list.
/// We therefore assert against the static dictionary directly so that adding
/// or removing a provider key forces the test to be reviewed.
/// </summary>
[TestFixture]
public class ProviderSelectorUIHintHandlerTests
{
    private ProviderSelectorUIHintHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new ProviderSelectorUIHintHandler();
    }

    // -----------------------------------------------------------------
    // UISyntax / GetSupportsUIHint contract
    // -----------------------------------------------------------------

    [Test]
    public void UISyntax_IsLiteral()
    {
        // Provider list is stored as a plain comma-separated literal, not a
        // JSON document or expression — UISyntax must reflect that so the
        // Studio shows the literal-edit experience.
        _handler.UISyntax.Should().Be("Literal");
    }

    [Test]
    public void GetSupportsUIHint_ReturnsTrue_ForExactMatch()
    {
        _handler.GetSupportsUIHint("tamma-provider-selector").Should().BeTrue();
    }

    [Test]
    public void GetSupportsUIHint_IsCaseInsensitive()
    {
        _handler.GetSupportsUIHint("TAMMA-PROVIDER-SELECTOR").Should().BeTrue();
        _handler.GetSupportsUIHint("Tamma-Provider-Selector").Should().BeTrue();
    }

    [TestCase("json-editor")]
    [TestCase("provider-selector")]   // close-but-not-equal — the hint is namespaced
    [TestCase("multiline")]
    [TestCase("dropdown")]
    [TestCase("")]
    public void GetSupportsUIHint_ReturnsFalse_ForOtherHints(string uiHint)
    {
        _handler.GetSupportsUIHint(uiHint).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Provider registry contract — assert against the component's static list
    // -----------------------------------------------------------------

    [Test]
    public void ProviderSelector_AvailableProviders_IncludesCoreProviders()
    {
        // These keys MUST stay aligned with the IAIProvider registry on the
        // server side.  If a provider is renamed or removed, this test
        // forces a coordinated update.
        ProviderSelector.AvailableProviders.Should().ContainKey("anthropic");
        ProviderSelector.AvailableProviders.Should().ContainKey("openai");
        ProviderSelector.AvailableProviders.Should().ContainKey("openrouter");
        ProviderSelector.AvailableProviders.Should().ContainKey("google");
        ProviderSelector.AvailableProviders.Should().ContainKey("github-copilot");
    }

    [Test]
    public void ProviderSelector_AvailableProviders_HasNoBlankOrNullKeys()
    {
        // Defensive: a blank key would render as a selectable but invisible
        // option and silently break the comma-separated value contract.
        foreach (var kvp in ProviderSelector.AvailableProviders)
        {
            kvp.Key.Should().NotBeNullOrWhiteSpace();
            kvp.Value.Should().NotBeNullOrWhiteSpace("display name for provider '{0}' must be set", kvp.Key);
        }
    }

    // -----------------------------------------------------------------
    // DisplayInputEditor — render tree assertions
    // -----------------------------------------------------------------

    [Test]
    public void DisplayInputEditor_ReturnsNonNullRenderFragment()
    {
        var context = new DisplayInputEditorContext { Value = "anthropic", IsReadOnly = false };

        var fragment = _handler.DisplayInputEditor(context);

        fragment.Should().NotBeNull();
    }

    [Test]
    public void DisplayInputEditor_OpensProviderSelectorComponent()
    {
        var context = new DisplayInputEditorContext { Value = "anthropic,openai", IsReadOnly = false };

        var frames = RenderToFrames(_handler.DisplayInputEditor(context));

        var componentFrame = FirstComponentFrame(frames);
        componentFrame.ComponentType.Should().Be(typeof(ProviderSelector),
            "tamma-provider-selector hint must mount the Tamma ProviderSelector Blazor component");
    }

    [Test]
    public void DisplayInputEditor_PassesValueThrough_AsCommaSeparatedString()
    {
        const string csv = "anthropic,openai,google";
        var context = new DisplayInputEditorContext { Value = csv, IsReadOnly = false };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes.Should().ContainKey(nameof(ProviderSelector.Value));
        attributes[nameof(ProviderSelector.Value)].Should().Be(csv);
    }

    [Test]
    public void DisplayInputEditor_DefaultsValueToEmptyString_WhenContextValueIsNull()
    {
        // Contract: an unset provider input should render as "" (the
        // component then renders an empty multi-select) rather than crashing
        // or showing "null".
        var context = new DisplayInputEditorContext { Value = null, IsReadOnly = false };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes[nameof(ProviderSelector.Value)].Should().Be(string.Empty);
    }

    [Test]
    public void DisplayInputEditor_PassesIsReadOnlyThrough()
    {
        var context = new DisplayInputEditorContext { Value = "anthropic", IsReadOnly = true };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes.Should().ContainKey(nameof(ProviderSelector.IsReadOnly));
        attributes[nameof(ProviderSelector.IsReadOnly)].Should().Be(true);
    }

    [Test]
    public void DisplayInputEditor_WiresValueChangedEventCallback()
    {
        var context = new DisplayInputEditorContext
        {
            Value = "anthropic",
            IsReadOnly = false,
            OnValueChanged = _ => Task.CompletedTask,
        };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes.Should().ContainKey(nameof(ProviderSelector.ValueChanged));
        var bound = attributes[nameof(ProviderSelector.ValueChanged)];
        bound.Should().NotBeNull();
        bound!.GetType().Name.Should().Be("EventCallback`1");
    }

    // -----------------------------------------------------------------
    // helpers (mirrors JsonEditorUIHintHandlerTests; kept colocated to
    // make each test class read top-to-bottom without cross-references)
    // -----------------------------------------------------------------

    private static ArrayRange<RenderTreeFrame> RenderToFrames(Microsoft.AspNetCore.Components.RenderFragment fragment)
    {
        var builder = new RenderTreeBuilder();
        fragment(builder);
        return builder.GetFrames();
    }

    private static RenderTreeFrame FirstComponentFrame(ArrayRange<RenderTreeFrame> frames)
    {
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames.Array[i].FrameType == RenderTreeFrameType.Component)
                return frames.Array[i];
        }

        Assert.Fail("Expected at least one Component frame in the render tree.");
        return default; // unreachable
    }

    private static IDictionary<string, object?> AttributeMap(ArrayRange<RenderTreeFrame> frames)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames.Array[i].FrameType == RenderTreeFrameType.Attribute)
            {
                map[frames.Array[i].AttributeName] = frames.Array[i].AttributeValue;
            }
        }

        return map;
    }
}
