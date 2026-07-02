using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Guardrails;

/// <summary>
/// Story 38-4 (AC7 + AC8) — the reflection-backed backstop for the TAMMA001 Roslyn
/// analyzer. It:
/// <list type="bullet">
///   <item>loads the REAL built <c>Tamma.Activities</c> / <c>Tamma.ElsaServer</c> assemblies
///     and asserts no type's constructor parameter / field / property is a denylisted
///     vendor-credential type (proving the post-cutover engine surface is clean) — except
///     the documented design-§5.3 exemptions;</item>
///   <item>asserts the analyzer is WIRED into both engine projects as an
///     <c>OutputItemType="Analyzer"</c> reference (so the gate is actually active); and</item>
///   <item>asserts NO <c>TAMMA001</c> suppression exists anywhere under the engine surface
///     (AC8 — the gate can't be quietly turned off).</item>
/// </list>
/// This is an INDEPENDENT check: the denylist below is hardcoded (not imported from the
/// analyzer's internal Allowlist), so weakening the analyzer would not weaken this backstop.
/// </summary>
[TestFixture]
public class ActivitiesGuardrailTests
{
    // Independent mirror of the vendor-credential INJECTION denylist. Epic 38 Phase 3: the
    // composite Tamma.Core.Interfaces.IIntegrationService and every focused variant are now
    // denied as engine injections (the engine reaches those domains only via TammaApiClient).
    private static readonly HashSet<string> Denylist = new(StringComparer.Ordinal)
    {
        "Octokit.IGitHubClient",
        "Octokit.GitHubClient",
        "Tamma.Activities.AgentDispatch.IGitHubActionsClient",
        "Tamma.Core.Interfaces.IIntegrationService",
        "Tamma.Core.Interfaces.IGitHubIntegrationService",
        "Tamma.Core.Interfaces.ISlackIntegrationService",
        "Tamma.Core.Interfaces.ICIIntegrationService",
        "Tamma.Core.Interfaces.IJiraIntegrationService",
        "Tamma.Core.Interfaces.IEmailIntegrationService",
        "Tamma.Activities.LlmCall.Credentials.IProviderCredentialResolver",
        "SlackNet.ISlackApiClient",
        "Stripe.StripeClient",
        "Stripe.IStripeClient",
    };

    // Design-§5.3 exemptions (local process / local filesystem / inbound signal).
    private static readonly HashSet<string> Exempt = new(StringComparer.Ordinal)
    {
        "Tamma.Providers.ICLIAgentProvider",
        "Tamma.Activities.LlmCall.Tools.FileReadTool",
        "Tamma.Activities.LlmCall.Tools.ShellExecuteTool",
        "Tamma.Activities.LlmCall.Tools.GitOperationsTool",
        "Tamma.Activities.AgentDispatch.WebhookSignalRegistry",
    };

    private const BindingFlags AllMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    [Test]
    public void TammaActivitiesAssembly_HasNoDenylistedVendorInjection()
        => AssertAssemblyClean(typeof(Tamma.Activities.LlmCall.TammaApiClient).Assembly);

    [Test]
    public void TammaElsaServerAssembly_HasNoDenylistedVendorInjection()
        => AssertAssemblyClean(Assembly.Load("Tamma.ElsaServer"));

    private static void AssertAssemblyClean(Assembly assembly)
    {
        foreach (var type in SafeGetTypes(assembly))
        {
            if (type is null || IsExempt(type) || IsCompilerGenerated(type))
                continue;

            foreach (var ctor in type.GetConstructors(AllMembers))
                foreach (var p in ctor.GetParameters())
                    Assert.That(Denylisted(p.ParameterType), Is.False,
                        $"{type.FullName}: constructor injects denylisted vendor type " +
                        $"'{p.ParameterType.FullName}' (rule-1 violation). Route the effect " +
                        "through Tamma.Api via TammaApiClient.");

            foreach (var f in type.GetFields(AllMembers))
                Assert.That(Denylisted(f.FieldType), Is.False,
                    $"{type.FullName}: field '{f.Name}' holds denylisted vendor type " +
                    $"'{f.FieldType.FullName}' (rule-1 violation).");

            foreach (var pr in type.GetProperties(AllMembers))
                Assert.That(Denylisted(pr.PropertyType), Is.False,
                    $"{type.FullName}: property '{pr.Name}' holds denylisted vendor type " +
                    $"'{pr.PropertyType.FullName}' (rule-1 violation).");
        }
    }

    [Test]
    public void Analyzer_IsWiredInto_BothEngineProjects()
    {
        foreach (var rel in new[]
        {
            Path.Combine("src", "Tamma.Activities", "Tamma.Activities.csproj"),
            Path.Combine("src", "Tamma.ElsaServer", "Tamma.ElsaServer.csproj"),
        })
        {
            var path = Path.Combine(RepoRoot(), rel);
            Assert.That(File.Exists(path), Is.True, $"missing {rel}");
            var text = File.ReadAllText(path);
            Assert.That(text.Contains("Tamma.Activities.Guardrails"), Is.True,
                $"{rel} does not reference the guardrail analyzer project.");
            Assert.That(text.Contains("OutputItemType=\"Analyzer\""), Is.True,
                $"{rel} does not reference the guardrail as an Analyzer (gate inactive).");
        }
    }

    [Test]
    public void NoTamma001Suppression_UnderEngineSurface()
    {
        var offenders = ScanForTamma001Suppression(RepoRoot());

        Assert.That(offenders, Is.Empty,
            "TAMMA001 must never be suppressed under the engine surface:\n" +
            string.Join("\n", offenders));
    }

    // AC8 counter-proof — plant every silent-off vector and assert the scan DETECTS each, so
    // NoTamma001Suppression_UnderEngineSurface can actually fail (empirically the .editorconfig
    // severity=none and the <NoWarn>TAMMA001 both disable the gate while an inline-only scan
    // stays green).
    [Test]
    public void SuppressionScan_DetectsPlantedEditorconfigNoWarnAndPragma()
    {
        var temp = Path.Combine(
            Path.GetTempPath(), "tamma-guardrail-suppress-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "src", "Tamma.Activities"));

            // (1) .editorconfig severity override at the apps/tamma-elsa root.
            File.WriteAllText(Path.Combine(temp, ".editorconfig"),
                "root = true\n\n[*.cs]\ndotnet_diagnostic.TAMMA001.severity = none\n");
            // (2) <NoWarn> in the root Directory.Build.props (the file that already carries NU1605).
            File.WriteAllText(Path.Combine(temp, "Directory.Build.props"),
                "<Project><PropertyGroup><NoWarn>$(NoWarn);NU1605;TAMMA001</NoWarn></PropertyGroup></Project>\n");
            // (3) #pragma under the engine src surface.
            File.WriteAllText(Path.Combine(temp, "src", "Tamma.Activities", "Evil.cs"),
                "#pragma warning disable TAMMA001\npublic class Evil { }\n");

            var offenders = ScanForTamma001Suppression(temp);

            Assert.Multiple(() =>
            {
                Assert.That(offenders.Any(o => o.Contains(".editorconfig")), Is.True,
                    "planted .editorconfig 'dotnet_diagnostic.TAMMA001.severity = none' was NOT detected");
                Assert.That(offenders.Any(o => o.Contains("Directory.Build.props")), Is.True,
                    "planted '<NoWarn>...TAMMA001</NoWarn>' was NOT detected");
                Assert.That(offenders.Any(o => o.Contains("Evil.cs")), Is.True,
                    "planted '#pragma warning disable TAMMA001' was NOT detected");
            });
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    // ----- helpers -----------------------------------------------------------------------

    /// <summary>
    /// Scans an <c>apps/tamma-elsa</c> root for ANY way TAMMA001 could be silently turned off:
    /// <list type="bullet">
    ///   <item>an <c>.editorconfig</c>/<c>.globalconfig</c> (anywhere from the root down)
    ///     setting <c>dotnet_diagnostic.TAMMA001.severity</c> to <c>none</c>/<c>silent</c>/<c>suppress</c>;</item>
    ///   <item>a <c>Directory.Build.props</c>/<c>Directory.Build.targets</c>/<c>.csproj</c>
    ///     (anywhere from the root down, so a root <c>Directory.Build.props</c> is in scope)
    ///     whose <c>NoWarn</c> contains TAMMA001;</item>
    ///   <item>a <c>#pragma warning disable TAMMA001</c> / <c>SuppressMessage</c> in a
    ///     <c>.cs</c> file under the engine src surface.</item>
    /// </list>
    /// Independent of the analyzer's internal Allowlist — pure string inspection.
    /// </summary>
    private static List<string> ScanForTamma001Suppression(string elsaRoot)
    {
        var offenders = new List<string>();

        // (a) build-config files anywhere from the root down.
        foreach (var file in EnumerateFilesSkippingBuildDirs(elsaRoot))
        {
            var name = Path.GetFileName(file);

            if (name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
                name.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadAllLines(file))
                    if (IsEditorConfigTamma001Suppression(line))
                        offenders.Add($"{file}: {line.Trim()}");
            }
            else if (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var line in File.ReadAllLines(file))
                    if (ContainsNoWarnTamma001(line))
                        offenders.Add($"{file}: {line.Trim()}");
            }
        }

        // (b) #pragma / SuppressMessage in .cs under the engine src surface only.
        foreach (var engineDir in new[]
        {
            Path.Combine(elsaRoot, "src", "Tamma.Activities"),
            Path.Combine(elsaRoot, "src", "Tamma.ElsaServer"),
        })
        {
            foreach (var file in EnumerateFilesSkippingBuildDirs(engineDir))
            {
                if (!file.EndsWith(".cs", StringComparison.Ordinal))
                    continue;
                foreach (var line in File.ReadAllLines(file))
                {
                    if (!line.Contains("TAMMA001"))
                        continue;
                    var isSuppression =
                        (line.Contains("#pragma") && line.Contains("warning") && line.Contains("disable")) ||
                        line.Contains("SuppressMessage");
                    if (isSuppression)
                        offenders.Add($"{file}: {line.Trim()}");
                }
            }
        }

        return offenders;
    }

    /// <summary>True for an ACTIVE (non-comment) editorconfig/globalconfig line that sets
    /// <c>dotnet_diagnostic.TAMMA001.severity</c> to none/silent/suppress.</summary>
    private static bool IsEditorConfigTamma001Suppression(string rawLine)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';')
            return false; // blank / comment

        var normalized = new string(trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray())
            .ToLowerInvariant();
        const string prefix = "dotnet_diagnostic.tamma001.severity=";
        if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var value = normalized.Substring(prefix.Length);
        var comment = value.IndexOfAny(new[] { '#', ';' });
        if (comment >= 0)
            value = value.Substring(0, comment);
        return value is "none" or "silent" or "suppress";
    }

    /// <summary>True for a line that carries both a <c>NoWarn</c> token and TAMMA001 (the
    /// single-line <c>&lt;NoWarn&gt;$(NoWarn);TAMMA001&lt;/NoWarn&gt;</c> / MSBuild-property form).</summary>
    private static bool ContainsNoWarnTamma001(string line) =>
        line.IndexOf("NoWarn", StringComparison.OrdinalIgnoreCase) >= 0 &&
        line.IndexOf("TAMMA001", StringComparison.Ordinal) >= 0;

    private static IEnumerable<string> EnumerateFilesSkippingBuildDirs(string dir)
    {
        if (!Directory.Exists(dir))
            yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            yield return file;
        }
    }

    private static bool Denylisted(Type t) =>
        t.FullName is { } name && Denylist.Contains(StripNullable(name));

    private static string StripNullable(string fullName)
    {
        // Nullable<T> value types surface as System.Nullable`1[[...]]; the denylist has no
        // value types, so only the plain FQN prefix matters here.
        var tick = fullName.IndexOf('`');
        return tick >= 0 ? fullName.Substring(0, tick) : fullName;
    }

    private static bool IsExempt(Type type)
    {
        if (type.FullName is { } fqn && Exempt.Contains(fqn))
            return true;
        if (type.GetInterfaces().Any(i => i.FullName is { } n && Exempt.Contains(n)))
            return true;
        for (var b = type.BaseType; b is not null; b = b.BaseType)
            if (b.FullName is { } bn && Exempt.Contains(bn))
                return true;
        return false;
    }

    private static bool IsCompilerGenerated(Type type) =>
        type.Name.Contains('<') ||
        type.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is not null;

    private static IEnumerable<Type?> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types; }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Tamma.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate apps/tamma-elsa (Tamma.sln) from " + AppContext.BaseDirectory);
    }
}
