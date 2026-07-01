using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Tamma.Activities.Guardrails.Tests;

/// <summary>
/// Story 38-4 — thin wrapper over the Roslyn analyzer-testing harness
/// (<see cref="CSharpAnalyzerTest{TAnalyzer, TVerifier}"/> + <see cref="DefaultVerifier"/>).
/// The engine-surface guard keys off <c>Compilation.AssemblyName</c>, so each run renames
/// the test project's assembly to a real engine name (or <c>Tamma.Api</c> for the
/// non-engine negative control) via a solution transform. Expected diagnostics are declared
/// inline with <c>{|TAMMA001:...|}</c> markup; a source with no markup asserts zero.
/// </summary>
internal static class Verify
{
    /// <summary>Analyze <paramref name="source"/> as the <c>Tamma.Activities</c> engine assembly.</summary>
    public static Task Engine(string source) => Run(source, "Tamma.Activities");

    /// <summary>Analyze <paramref name="source"/> as the <c>Tamma.ElsaServer</c> engine assembly.</summary>
    public static Task ElsaServer(string source) => Run(source, "Tamma.ElsaServer");

    /// <summary>Analyze <paramref name="source"/> as <c>Tamma.Api</c> (NOT the engine surface).</summary>
    public static Task Api(string source) => Run(source, "Tamma.Api");

    private static async Task Run(string source, string assemblyName)
    {
        var test = new CSharpAnalyzerTest<EngineExternalCallAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
        };
        test.TestState.OutputKind = OutputKind.DynamicallyLinkedLibrary;
        test.SolutionTransforms.Add((solution, projectId) =>
            solution.WithProjectAssemblyName(projectId, assemblyName));
        await test.RunAsync();
    }
}
