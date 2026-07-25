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
        inputAttr!.UIHint.Should().Be("json-editor",
            "ToolsJson should use the json-editor UI hint for JSON editing");
    }

    [Test]
    public void WaitForPlanApprovalActivity_PlanJson_HasJsonEditorUIHint()
    {
        var property = typeof(WaitForPlanApprovalActivity).GetProperty("PlanJson");
        property.Should().NotBeNull("WaitForPlanApprovalActivity must have a PlanJson property");

        var inputAttr = property!.GetCustomAttribute<InputAttribute>();
        inputAttr.Should().NotBeNull("PlanJson must have an [Input] attribute");
        inputAttr!.UIHint.Should().Be("json-editor",
            "PlanJson should use the json-editor UI hint for JSON editing");
    }

    // ResolveLlmPromptActivity_SystemPromptOverride_HasMultiLineUIHint was removed
    // with the activity itself — the abandoned config-driven prompt hierarchy. The
    // live resolver is ResolvePromptFromRegistryActivity; the surviving multiline
    // hint assertion below covers ResolveAgentConfigActivity's override property.

    [Test]
    public void ResolveAgentConfigActivity_SystemPromptOverrideProp_HasMultiLineUIHint()
    {
        var property = typeof(ResolveAgentConfigActivity).GetProperty("SystemPromptOverrideProp");
        property.Should().NotBeNull("ResolveAgentConfigActivity must have a SystemPromptOverrideProp property");

        var inputAttr = property!.GetCustomAttribute<InputAttribute>();
        inputAttr.Should().NotBeNull("SystemPromptOverrideProp must have an [Input] attribute");
        inputAttr!.UIHint.Should().Be("multiline",
            "SystemPromptOverrideProp should use the multiline UI hint for long text");
    }

    [Test]
    public void CallLlmActivity_NonJsonInputs_DoNotHaveJsonEditorHint()
    {
        // Verify other inputs on CallLlmActivity do NOT accidentally get the JSON editor hint
        var nonJsonProperties = new[]
        {
            "ProviderName", "SystemPrompt", "UserPrompt",
            "ModelOverride", "MaxTokens", "Temperature", "AttemptNumber"
        };

        foreach (var propName in nonJsonProperties)
        {
            var property = typeof(CallLlmActivity).GetProperty(propName);
            if (property == null) continue;

            var inputAttr = property.GetCustomAttribute<InputAttribute>();
            if (inputAttr?.UIHint != null)
            {
                inputAttr.UIHint.Should().NotBe("json-editor",
                    $"{propName} should not use the JSON editor hint");
            }
        }
    }
}
