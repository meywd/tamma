using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Tamma.Activities.Guardrails;

/// <summary>
/// Story 38-4 — the rule-1 guardrail. Reports <c>TAMMA001</c> (Error) when an
/// ENGINE-SURFACE type (<c>Tamma.Activities</c> / <c>Tamma.ElsaServer</c> only — never
/// <c>Tamma.Api</c>) does any of three things:
///
/// <list type="number">
///   <item><b>Direct external HTTP</b> — an <c>HttpClient</c> send whose statically
///     resolvable target host is an EXTERNAL host (i.e. not a loopback host and not an
///     un-resolvable, config-driven <c>TammaApiClient</c> / <c>Engine:CallbackUrl</c>
///     host). A non-literal-host raw <c>HttpClient</c> call is NOT re-scanned for a host;
///     it is instead caught by the injection / service-locator / construction passes below —
///     the credential to reach a real vendor has to ARRIVE somehow (an injected client, a
///     container resolve, or a constructed vendor SDK). The one residual gap (documented) is
///     a fully-untyped credential reaching a non-literal URL — no static seam exists to key
///     on, so that narrow case is not caught by this analyzer.</item>
///   <item><b>Vendor-credential injection</b> — a constructor parameter, field, or
///     property whose type is on <see cref="Allowlist.InjectionDenylist"/>. METHOD
///     parameters are NOT injection (the reused static cores keep the git service as a
///     method param), so only ctor params + fields/properties are inspected.</item>
///   <item><b>Service-locator resolve</b> (FIX I1) — a call to
///     <c>GetService</c>/<c>GetRequiredService</c>/<c>GetKeyedService</c>/<c>GetRequiredKeyedService</c>
///     (MS.DI or Elsa's <c>context.GetService&lt;T&gt;()</c>) whose type argument (generic
///     <c>&lt;T&gt;</c> or the <c>typeof(T)</c>/<c>Type</c> overload) is on
///     <see cref="Allowlist.InjectionDenylist"/> — the dominant DI idiom here, evading the
///     ctor/field/property pass.</item>
///   <item><b>Vendor construction</b> (FIX M2) — <c>new T()</c> where <c>T</c> is a concrete
///     denylisted type (<c>Octokit.GitHubClient</c>, <c>Stripe.StripeClient</c>, ...), which
///     evades pass (2) unless stored in a denylisted-typed field.</item>
///   <item><b>Denied Slack send</b> — an invocation of
///     <c>SendSlackMessageAsync</c>/<c>SendSlackDirectMessageAsync</c> on any receiver
///     (Correction 2: closes the hole left by allowing the composite
///     <c>IIntegrationService</c> injection).</item>
/// </list>
///
/// The design-§5.3 exempt categories (<see cref="Allowlist.ExemptTypeNames"/>) are never
/// flagged. Uses the semantic model (not strings), so an alias / <c>using</c> rename can't
/// evade it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EngineExternalCallAnalyzer : DiagnosticAnalyzer
{
    private static readonly SymbolDisplayFormat FullyQualified = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None);

    private const string HttpMessageInvoker = "System.Net.Http.HttpMessageInvoker";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(GuardrailDiagnostics.EngineDirectExternalCall);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterCompilationStartAction(start =>
        {
            // Engine surface ONLY — Tamma.Api is supposed to hold credentials + call vendors.
            if (!Allowlist.IsEngineSurface(start.Compilation.AssemblyName))
                return;

            start.RegisterOperationAction(InspectInvocation, OperationKind.Invocation);
            start.RegisterOperationAction(InspectObjectCreation, OperationKind.ObjectCreation);
            start.RegisterSymbolAction(InspectConstructor, SymbolKind.Method);
            start.RegisterSymbolAction(InspectFieldOrProperty, SymbolKind.Field, SymbolKind.Property);
        });
    }

    // ----- (2) vendor-credential injection: constructor parameters -----------------------
    private static void InspectConstructor(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.MethodKind != MethodKind.Constructor)
            return;

        var owner = method.ContainingType;
        if (owner is null || IsExempt(owner))
            return;

        foreach (var parameter in method.Parameters)
        {
            var typeName = FullName(parameter.Type);
            if (Allowlist.InjectionDenylist.Contains(typeName))
            {
                var location = parameter.Locations.FirstOrDefault()
                               ?? method.Locations.FirstOrDefault()
                               ?? Location.None;
                context.ReportDiagnostic(Diagnostic.Create(
                    GuardrailDiagnostics.EngineDirectExternalCall, location, owner.Name, typeName));
            }
        }
    }

    // ----- (2) vendor-credential injection: fields + properties --------------------------
    private static void InspectFieldOrProperty(SymbolAnalysisContext context)
    {
        ITypeSymbol memberType;
        INamedTypeSymbol? owner;
        Location location;

        switch (context.Symbol)
        {
            case IFieldSymbol field:
                // Skip compiler-synthesized fields (e.g. auto-property backing fields); the
                // property itself is inspected separately.
                if (field.IsImplicitlyDeclared || field.AssociatedSymbol is IPropertySymbol)
                    return;
                memberType = field.Type;
                owner = field.ContainingType;
                location = field.Locations.FirstOrDefault() ?? Location.None;
                break;

            case IPropertySymbol property:
                memberType = property.Type;
                owner = property.ContainingType;
                location = property.Locations.FirstOrDefault() ?? Location.None;
                break;

            default:
                return;
        }

        if (owner is null || IsExempt(owner))
            return;

        var typeName = FullName(memberType);
        if (Allowlist.InjectionDenylist.Contains(typeName))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.EngineDirectExternalCall, location, owner.Name, typeName));
        }
    }

    // ----- (1) direct external HTTP + (3) denied Slack send ------------------------------
    private static void InspectInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;

        var enclosing = context.ContainingSymbol as INamedTypeSymbol
                        ?? context.ContainingSymbol?.ContainingType;
        if (enclosing is not null && IsExempt(enclosing))
            return;

        var target = invocation.TargetMethod;
        var enclosingName = enclosing?.Name ?? "<engine>";

        // (3) Denied Slack send on ANY receiver.
        if (Allowlist.DeniedInvocationNames.Contains(target.Name))
        {
            var receiver = target.ContainingType is { } ct ? $"{ct.Name}.{target.Name}" : target.Name;
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.EngineDirectExternalCall,
                invocation.Syntax.GetLocation(), enclosingName, receiver));
            return;
        }

        // (I1) Service-locator resolve of a denylisted vendor-credential type.
        if (Allowlist.ServiceLocatorMethodNames.Contains(target.Name))
        {
            var resolved = ResolvedServiceType(invocation);
            if (resolved is not null && Allowlist.InjectionDenylist.Contains(FullName(resolved)))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GuardrailDiagnostics.EngineDirectExternalCall,
                    invocation.Syntax.GetLocation(), enclosingName,
                    $"service-locator resolve of '{FullName(resolved)}'"));
                return;
            }
        }

        // (1) Direct external HTTP.
        if (!IsHttpSendMethod(target))
            return;

        foreach (var argument in invocation.Arguments)
        {
            var host = TryResolveExternalHost(argument.Value);
            if (host is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    GuardrailDiagnostics.EngineDirectExternalCall,
                    invocation.Syntax.GetLocation(), enclosingName, $"external host '{host}'"));
                return;
            }
        }
    }

    // ----- (M2) vendor construction: `new <denylisted-type>()` ---------------------------
    private static void InspectObjectCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;

        var enclosing = context.ContainingSymbol as INamedTypeSymbol
                        ?? context.ContainingSymbol?.ContainingType;
        if (enclosing is not null && IsExempt(enclosing))
            return;

        var typeName = FullName(creation.Type);
        if (Allowlist.InjectionDenylist.Contains(typeName))
        {
            var enclosingName = enclosing?.Name ?? "<engine>";
            context.ReportDiagnostic(Diagnostic.Create(
                GuardrailDiagnostics.EngineDirectExternalCall,
                creation.Syntax.GetLocation(), enclosingName, $"construction of '{typeName}'"));
        }
    }

    // ----- helpers -----------------------------------------------------------------------

    /// <summary>The service type a <c>GetService</c>/<c>GetRequiredService</c>/... call
    /// resolves: the generic <c>&lt;T&gt;</c> type argument, else the <c>typeof(T)</c> operand
    /// of the <c>Type</c>-arg overload. Null when neither is statically known.</summary>
    private static ITypeSymbol? ResolvedServiceType(IInvocationOperation invocation)
    {
        var typeArgs = invocation.TargetMethod.TypeArguments;
        if (typeArgs.Length == 1)
            return typeArgs[0];

        foreach (var argument in invocation.Arguments)
        {
            if (Unwrap(argument.Value) is ITypeOfOperation typeOf)
                return typeOf.TypeOperand;
        }

        return null;
    }

    private static bool IsHttpSendMethod(IMethodSymbol method)
    {
        if (!Allowlist.HttpSendMethodNames.Contains(method.Name))
            return false;

        var containing = method.ContainingType;
        if (containing is null)
            return false;

        if (Allowlist.HttpClientTypeNames.Contains(FullName(containing)))
            return true;

        // Instance sends (e.g. SendAsync) declared on HttpMessageInvoker / a subtype.
        for (var baseType = containing.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (FullName(baseType) == HttpMessageInvoker)
                return true;
        }

        return false;
    }

    /// <summary>Returns the EXTERNAL host of a URL argument when it is statically
    /// resolvable (a string constant; an interpolated string whose LEADING part is a literal
    /// absolute URL; or an inline <c>new HttpRequestMessage(method, "https://...")</c> /
    /// <c>new Uri("https://...")</c> whose literal URL is dug out). Returns null for loopback
    /// hosts and for any argument whose host cannot be resolved (a variable / config value /
    /// a leading interpolation). A non-literal host is NOT treated as a violation here — the
    /// credential to reach a real vendor has to arrive via an injected client, a
    /// service-locator resolve, or a constructed vendor SDK, all caught by the other passes;
    /// only a fully-untyped credential reaching a non-literal URL is a documented residual
    /// gap.</summary>
    private static string? TryResolveExternalHost(IOperation? value)
    {
        value = Unwrap(value);
        if (value is null)
            return null;

        // Dig a literal URL out of an inline `new HttpRequestMessage(method, "https://...")`
        // / `new Uri("https://...")` (the shape a sync/async Send takes) — the request/URI is
        // constructed at the call site, so its literal host is statically resolvable.
        if (value is IObjectCreationOperation creation)
        {
            foreach (var argument in creation.Arguments)
            {
                var nested = TryResolveExternalHost(argument.Value);
                if (nested is not null)
                    return nested;
            }
            return null;
        }

        string? text = null;

        if (value.ConstantValue.HasValue && value.ConstantValue.Value is string constant)
        {
            text = constant;
        }
        else if (value is IInterpolatedStringOperation interpolated)
        {
            var firstPart = interpolated.Parts.FirstOrDefault();
            if (firstPart is IInterpolatedStringTextOperation textPart &&
                textPart.Text.ConstantValue.HasValue &&
                textPart.Text.ConstantValue.Value is string leading)
            {
                text = leading;
            }
            else
            {
                // Leading interpolation (e.g. $"{callbackUrl}/...") — host not resolvable.
                return null;
            }
        }

        if (string.IsNullOrEmpty(text))
            return null;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        var host = uri.Host;
        if (string.IsNullOrEmpty(host) || IsLoopback(host))
            return null;

        return host;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host == "127.0.0.1"
        || host == "0.0.0.0"
        || host == "::1"
        || host == "[::1]"
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;
        return operation;
    }

    private static bool IsExempt(INamedTypeSymbol? type)
    {
        if (type is null)
            return false;

        if (Allowlist.ExemptTypeNames.Contains(FullName(type)))
            return true;

        foreach (var iface in type.AllInterfaces)
        {
            if (Allowlist.ExemptTypeNames.Contains(FullName(iface)))
                return true;
        }

        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (Allowlist.ExemptTypeNames.Contains(FullName(baseType)))
                return true;
        }

        return false;
    }

    private static string FullName(ITypeSymbol? type) =>
        type is null ? string.Empty : type.OriginalDefinition.ToDisplayString(FullyQualified);
}
