using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;

namespace Tamma.Activities.Tests.AgentDispatch;

/// <summary>
/// Story 38-2 (AC9) — the cutover proof: after the Class-C mediation, NO
/// <c>Tamma.Activities</c> agent-dispatch type INJECTS the credential-holding
/// <see cref="IGitHubActionsClient"/> (no constructor param, no field, no
/// <c>GetService&lt;IGitHubActionsClient&gt;()</c> service-locator). The interface +
/// <c>NullGitHubActionsClient</c> may remain as FILES (the API's Octokit impl
/// implements the interface); AC9 forbids only INJECTIONS in the engine.
/// </summary>
[TestFixture]
public class AgentDispatchCutoverTests
{
    private static readonly Type[] AgentDispatchTypes =
    {
        typeof(AgentDispatchService),
        typeof(AgentMonitorService),
        typeof(AgentResultCollectorService),
        typeof(GitHubActionsExecutor),
        typeof(DispatchAgentWorkflowActivity),
        typeof(MonitorAgentWorkflowActivity),
        typeof(CollectAgentResultsActivity),
    };

    [Test]
    public void NoAgentDispatchType_HasIGitHubActionsClientConstructorParameter()
    {
        foreach (var type in AgentDispatchTypes)
        {
            foreach (var ctor in type.GetConstructors())
            {
                ctor.GetParameters()
                    .Any(p => typeof(IGitHubActionsClient).IsAssignableFrom(p.ParameterType))
                    .Should().BeFalse($"{type.Name} must not inject IGitHubActionsClient via its constructor");
            }
        }
    }

    [Test]
    public void NoAgentDispatchType_HasIGitHubActionsClientField()
    {
        foreach (var type in AgentDispatchTypes)
        {
            type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Any(f => typeof(IGitHubActionsClient).IsAssignableFrom(f.FieldType))
                .Should().BeFalse($"{type.Name} must hold no IGitHubActionsClient field");
        }
    }

    [Test]
    public void NoActivitySource_ResolvesIGitHubActionsClientFromDi()
    {
        var activitiesDir = FindActivitiesRoot();
        activitiesDir.Should().NotBeNull("the Tamma.Activities source root should be locatable from the test run");

        var serviceLocatorOffenders = Directory.EnumerateFiles(activitiesDir!, "*.cs", SearchOption.AllDirectories)
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                return text.Contains("GetService<IGitHubActionsClient>")
                    || text.Contains("GetRequiredService<IGitHubActionsClient>");
            })
            .Select(Path.GetFileName)
            .ToList();
        serviceLocatorOffenders.Should().BeEmpty("no engine code may resolve IGitHubActionsClient from DI after the 38-2 cutover");
    }

    [Test]
    public void PhaseServiceAndActivitySources_HoldNoIGitHubActionsClientReferenceInCode()
    {
        var activitiesDir = FindActivitiesRoot();
        activitiesDir.Should().NotBeNull();

        var adDir = Path.Combine(activitiesDir!, "AgentDispatch");
        foreach (var name in new[]
        {
            "AgentDispatchService.cs", "AgentMonitorService.cs", "AgentResultCollectorService.cs",
            "DispatchAgentWorkflowActivity.cs", "MonitorAgentWorkflowActivity.cs", "CollectAgentResultsActivity.cs",
        })
        {
            // Strip comment lines (/// XML docs + // line comments) — doc comments
            // may legitimately mention the interface to explain the cutover; only
            // CODE references (a field/param declaration or service-locator call)
            // would be a real leftover injection.
            var codeLines = File.ReadLines(Path.Combine(adDir, name))
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
            var code = string.Join('\n', codeLines);
            code.Should().NotContain("IGitHubActionsClient",
                $"{name} must hold no IGitHubActionsClient reference in code after the cutover (it is a thin TammaApiClient client)");
        }
    }

    private static string? FindActivitiesRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tamma.Activities");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
