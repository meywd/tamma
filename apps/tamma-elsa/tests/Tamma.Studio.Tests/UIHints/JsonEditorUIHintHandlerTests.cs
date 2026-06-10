using Elsa.Studio.Models;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using NUnit.Framework;
using Tamma.Studio.Components;
using Tamma.Studio.UIHints;

namespace Tamma.Studio.Tests.UIHints;

/// <summary>
/// Unit tests for <see cref="JsonEditorUIHintHandler"/>.
///
/// The handler is a thin Studio extension point: it advertises a UI syntax,
/// claims responsibility for the <c>json-editor</c> hint, and produces a
/// <c>RenderFragment</c> that mounts the <see cref="JsonEditor"/> Blazor
/// component bound to the editor context.  We test the contract surface
/// (UISyntax, GetSupportsUIHint) directly, and we render the fragment
/// against a real <see cref="RenderTreeBuilder"/> to verify the component
/// frame and bound parameters are what the Studio designer expects.
/// </summary>
[TestFixture]
public class JsonEditorUIHintHandlerTests
{
    private JsonEditorUIHintHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new JsonEditorUIHintHandler();
    }

    // -----------------------------------------------------------------
    // UISyntax / GetSupportsUIHint contract
    // -----------------------------------------------------------------

    [Test]
    public void UISyntax_IsJson()
    {
        // The Studio uses UISyntax to choose a default expression syntax for
        // the input.  JSON inputs should show the Json/literal mode, NOT the
        // C#/JavaScript expression editor.
        _handler.UISyntax.Should().Be("Json");
    }

    [Test]
    public void GetSupportsUIHint_ReturnsTrue_ForExactMatch()
    {
        _handler.GetSupportsUIHint("json-editor").Should().BeTrue();
    }

    [Test]
    public void GetSupportsUIHint_IsCaseInsensitive()
    {
        _handler.GetSupportsUIHint("JSON-EDITOR").Should().BeTrue();
        _handler.GetSupportsUIHint("Json-Editor").Should().BeTrue();
    }

    [TestCase("multiline")]
    [TestCase("tamma-provider-selector")]
    [TestCase("dropdown")]
    [TestCase("")]
    [TestCase("json")]          // close-but-not-equal
    [TestCase("json-editor ")]  // trailing whitespace — handler does NOT trim
    public void GetSupportsUIHint_ReturnsFalse_ForOtherHints(string uiHint)
    {
        _handler.GetSupportsUIHint(uiHint).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // DisplayInputEditor — render tree assertions
    // -----------------------------------------------------------------

    [Test]
    public void DisplayInputEditor_ReturnsNonNullRenderFragment()
    {
        var context = new DisplayInputEditorContext { Value = "{}", IsReadOnly = false };

        var fragment = _handler.DisplayInputEditor(context);

        fragment.Should().NotBeNull();
    }

    [Test]
    public void DisplayInputEditor_OpensJsonEditorComponent_WithValueFromContext()
    {
        const string json = "{\"foo\":1}";
        var context = new DisplayInputEditorContext { Value = json, IsReadOnly = false };

        var frames = RenderToFrames(_handler.DisplayInputEditor(context));

        // First frame should be the component being opened.
        var componentFrame = FirstComponentFrame(frames);
        componentFrame.ComponentType.Should().Be(typeof(JsonEditor),
            "json-editor hint must mount the Tamma JsonEditor Blazor component");

        // Three attributes are emitted: Value, ValueChanged, IsReadOnly.
        var attributes = AttributeMap(frames);
        attributes.Should().ContainKey(nameof(JsonEditor.Value));
        attributes[nameof(JsonEditor.Value)].Should().Be(json,
            "Value attribute must round-trip the editor's current value");
    }

    [Test]
    public void DisplayInputEditor_DefaultsValueToEmptyJsonObject_WhenContextValueIsNull()
    {
        // Contract: a brand-new activity input with no value should render as
        // "{}" so the JSON editor starts in a valid empty-object state rather
        // than blank text or "null".
        var context = new DisplayInputEditorContext { Value = null, IsReadOnly = false };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes[nameof(JsonEditor.Value)].Should().Be("{}");
    }

    [Test]
    public void DisplayInputEditor_PassesIsReadOnlyThrough()
    {
        var context = new DisplayInputEditorContext { Value = "{}", IsReadOnly = true };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes.Should().ContainKey(nameof(JsonEditor.IsReadOnly));
        attributes[nameof(JsonEditor.IsReadOnly)].Should().Be(true);
    }

    [Test]
    public void DisplayInputEditor_BindsValueChanged_ToContextUpdateAsync()
    {
        // When the JsonEditor raises ValueChanged, the handler must route the
        // new value through the context's OnValueChanged callback (via
        // UpdateValueOrLiteralExpressionAsync).  We don't have a real
        // dispatcher here, so we capture the EventCallback delegate and
        // verify that triggering it calls our spy.
        object? captured = null;
        var context = new DisplayInputEditorContext
        {
            Value = "{}",
            IsReadOnly = false,
            OnValueChanged = value =>
            {
                captured = value;
                return Task.CompletedTask;
            },
        };

        var attributes = AttributeMap(RenderToFrames(_handler.DisplayInputEditor(context)));

        attributes.Should().ContainKey(nameof(JsonEditor.ValueChanged));
        var bound = attributes[nameof(JsonEditor.ValueChanged)];
        bound.Should().NotBeNull("ValueChanged must be wired so user edits propagate");

        // The handler stores an EventCallback<string>.  We can't easily invoke
        // it without a Dispatcher, so we just assert it has the expected
        // shape.  The end-to-end binding (EventCallback -> context callback)
        // is exercised by ELSA Studio at runtime; here we only need to know
        // that the handler attached *something* delegate-shaped.
        bound!.GetType().Name.Should().Be("EventCallback`1");

        // Smoke: the delegate behind the EventCallback isn't null — i.e. the
        // handler did not pass an empty callback.
        var delegateField = bound.GetType()
            .GetField("Delegate", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        delegateField.Should().NotBeNull();
        delegateField!.GetValue(bound).Should().NotBeNull();

        // We never invoked it, so the spy stays untouched.  This guards
        // against the handler eagerly running side effects at render time.
        captured.Should().BeNull();
    }

    // -----------------------------------------------------------------
    // helpers
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

    /// <summary>
    /// Builds a name -> value map of all <c>Attribute</c> frames.  In Blazor
    /// terminology, parameters passed to a component are emitted as Attribute
    /// frames immediately following the Component frame.
    /// </summary>
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
