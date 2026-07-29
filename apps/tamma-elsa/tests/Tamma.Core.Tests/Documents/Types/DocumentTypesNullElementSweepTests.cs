using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1b follow-up finding 4 — the GENERIC proof that a null array element
/// fails CLOSED for every registered document type.
///
/// <para>
/// The defect: every type guarded only the top-level null document and then
/// dereferenced per-element fields inside its loops under a
/// <c>JsonException</c>-only catch, so <c>{"criteria":[null]}</c> /
/// <c>{"items":[null]}</c> escaped <c>Validate</c> as a
/// <see cref="NullReferenceException"/>. <c>DocumentLifecycleWorkflow</c> calls
/// <c>Validate</c> unguarded, so that FAULTED the run instead of routing to the
/// deterministic repair ring — a model emitting one null element lost the whole
/// workflow rather than earning a repair turn. The shape was identical in the
/// pre-existing types (Findings included), so the fix is the shared
/// <c>DocumentPayloadGuard</c> and the proof is this sweep, not per-type
/// hand-written cases.
/// </para>
///
/// <para>
/// The sweep is generic on purpose: it walks <see cref="DocumentTypeRegistry.All"/>,
/// discovers each type's array-valued members by reflection over its
/// <see cref="IDocumentType.PayloadClrType"/> (top level AND one level of nesting),
/// AND injects a null into every array of every VALID shipped example. A type
/// added later inherits the coverage — and goes red here if its author forgets the
/// guard, instead of shipping the fault.
/// </para>
/// </summary>
[TestFixture]
public class DocumentTypesNullElementSweepTests
{
    /// <summary>The one code every type reports for a structurally malformed payload.</summary>
    private const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>
    /// A sibling-document context rich enough to drive EVERY cross-document reader
    /// past its fail-soft early return: <c>subtasks</c> for AcceptanceCriteria's
    /// decomposition reader, <c>tasks</c> for TestSpec's plan reader, and
    /// <c>criteria</c> for UxSpec's acceptance-criteria reader. Without it the
    /// <c>ValidateWithContext</c> half of the sweep would never reach the
    /// cross-document loops that dereference per-element fields.
    /// </summary>
    private const string SiblingContext =
        """
        {
          "subtasks": [ { "id": "ST-1" } ],
          "tasks": [ { "id": "T-1" } ],
          "criteria": [ { "id": "AC-1" } ]
        }
        """;

    /// <summary>One generated payload: which type it targets and where the null sits.</summary>
    public sealed record Probe(string TypeKey, string Where, string Json)
    {
        public override string ToString() => $"{TypeKey} :: {Where}";
    }

    // ── the property ─────────────────────────────────────────────────────────

    [TestCaseSource(nameof(Probes))]
    public void Null_array_element_is_MALFORMED_PAYLOAD_never_a_throw(Probe probe)
    {
        var type = DocumentTypeRegistry.Resolve(probe.TypeKey);
        using var document = JsonDocument.Parse(probe.Json);
        var payload = document.RootElement;

        var because = $"{probe.TypeKey} must fail closed on a null element at {probe.Where} ({probe.Json})";

        DocumentValidationResult? result = null;
        var validate = () => result = type.Validate(payload);
        validate.Should().NotThrow(because);

        result!.IsValid.Should().BeFalse(because);
        result.Violations.Select(v => v.Code).Should().Contain(MalformedPayload, because);

        // The cross-document seam is the same entry point for the lifecycle's
        // VALIDATE stage and must fail closed identically — including for the three
        // types that override it and run their own per-element loops.
        DocumentValidationResult? contextResult = null;
        var validateWithContext = () => contextResult = type.ValidateWithContext(payload, SiblingContext);
        validateWithContext.Should().NotThrow(because);

        contextResult!.IsValid.Should().BeFalse(because);
        contextResult.Violations.Select(v => v.Code).Should().Contain(MalformedPayload, because);
    }

    // ── the sweep may not silently degenerate ────────────────────────────────

    [Test]
    public void Sweep_probes_every_registered_type_that_declares_an_array_member()
    {
        var typesWithArrays = DocumentTypeRegistry.All
            .Where(t => ArrayMembersOf(t.PayloadClrType).Count > 0)
            .Select(t => t.Key)
            .ToList();

        var probed = Probes().Select(p => p.TypeKey).Distinct().ToList();

        probed.Should().Contain(typesWithArrays,
            "a registered type with an array-valued member must be swept for null elements");

        // Floor, not a pin: 16 of the 17 registered types declare an array member
        // (prose is body-only markdown). A reflection change that silently stops
        // discovering members would otherwise turn the sweep into a no-op.
        typesWithArrays.Count.Should().BeGreaterThanOrEqualTo(16,
            "the sweep must keep covering the array-bearing document types");
    }

    [Test]
    public void Sweep_covers_the_reviewer_cited_payload_shapes()
    {
        var probed = Probes().Select(p => p.Json.Replace(" ", "")).ToList();

        probed.Should().Contain("""{"criteria":[null]}""");
        probed.Should().Contain("""{"items":[null]}""");
    }

    // ── probe generation ─────────────────────────────────────────────────────

    /// <summary>
    /// Every generated probe: for each registered type, a null element in each
    /// array-valued member of its payload CLR type (and of each nested element
    /// type), plus a null appended to every array inside each of its VALID shipped
    /// examples — the nesting the flat reflection pass cannot reach.
    /// </summary>
    public static IReadOnlyList<Probe> Probes() => s_probes;

    private static readonly IReadOnlyList<Probe> s_probes = BuildProbes();

    private static IReadOnlyList<Probe> BuildProbes()
    {
        var probes = new List<Probe>();

        foreach (var type in DocumentTypeRegistry.All)
        {
            foreach (var member in ArrayMembersOf(type.PayloadClrType))
            {
                probes.Add(new Probe(
                    type.Key,
                    $"{member.Wire}[0]",
                    new JsonObject { [member.Wire] = NullArray() }.ToJsonString()));

                foreach (var nested in ArrayMembersOf(member.ElementType))
                {
                    var element = new JsonObject { [nested.Wire] = NullArray() };
                    probes.Add(new Probe(
                        type.Key,
                        $"{member.Wire}[0].{nested.Wire}[0]",
                        new JsonObject { [member.Wire] = new JsonArray(element) }.ToJsonString()));
                }
            }

            foreach (var example in type.Examples.Where(e => e.IsValid))
            {
                foreach (var (where, json) in NullInjections(example.PayloadJson))
                    probes.Add(new Probe(type.Key, $"example '{example.Name}' @ {where}", json));
            }
        }

        return probes;
    }

    private static JsonArray NullArray() => new(new JsonNode?[] { null });

    // ── reflection over the payload CLR shape ────────────────────────────────

    private sealed record ArrayMember(string Wire, Type ElementType);

    private static IReadOnlyList<ArrayMember> ArrayMembersOf(Type? clrType)
    {
        var members = new List<ArrayMember>();
        if (clrType is null || clrType.IsPrimitive || clrType == typeof(string))
            return members;

        foreach (var property in clrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var elementType = ElementTypeOf(property.PropertyType);
            if (elementType is null)
                continue;

            var wire = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            members.Add(new ArrayMember(wire, elementType));
        }

        return members;
    }

    private static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
            return null;

        if (type.IsArray)
            return type.GetElementType();

        if (!type.IsGenericType)
            return null;

        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IReadOnlyList<>)
            || definition == typeof(IReadOnlyCollection<>)
            || definition == typeof(IList<>)
            || definition == typeof(ICollection<>)
            || definition == typeof(IEnumerable<>)
            || definition == typeof(List<>)
                ? type.GetGenericArguments()[0]
                : null;
    }

    // ── example mutation: a null appended to every array in a valid example ──

    private static IEnumerable<(string Where, string Json)> NullInjections(string exampleJson)
    {
        var paths = new List<IReadOnlyList<object>>();
        CollectArrayPaths(JsonNode.Parse(exampleJson), new List<object>(), paths);

        foreach (var path in paths)
        {
            var mutated = JsonNode.Parse(exampleJson)!;
            ((JsonArray)Navigate(mutated, path)).Add((JsonNode?)null);
            yield return (Describe(path), mutated.ToJsonString());
        }
    }

    private static void CollectArrayPaths(JsonNode? node, List<object> prefix, List<IReadOnlyList<object>> found)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    prefix.Add(key);
                    CollectArrayPaths(value, prefix, found);
                    prefix.RemoveAt(prefix.Count - 1);
                }
                break;

            case JsonArray array:
                found.Add(prefix.ToArray());
                for (var i = 0; i < array.Count; i++)
                {
                    prefix.Add(i);
                    CollectArrayPaths(array[i], prefix, found);
                    prefix.RemoveAt(prefix.Count - 1);
                }
                break;
        }
    }

    private static JsonNode Navigate(JsonNode root, IReadOnlyList<object> path)
    {
        var current = root;
        foreach (var segment in path)
            current = segment is string key ? current.AsObject()[key]! : current.AsArray()[(int)segment]!;
        return current;
    }

    private static string Describe(IReadOnlyList<object> path)
    {
        var text = new System.Text.StringBuilder();
        foreach (var segment in path)
        {
            if (segment is string key)
            {
                if (text.Length > 0)
                    text.Append('.');
                text.Append(key);
            }
            else
            {
                text.Append('[').Append(segment).Append(']');
            }
        }
        return text.Length == 0 ? "(root)" : text.ToString();
    }
}
