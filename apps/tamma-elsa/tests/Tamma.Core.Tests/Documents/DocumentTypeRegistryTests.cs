using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Core.Tests.Documents;

/// <summary>
/// Drift tests for the <see cref="DocumentTypeRegistry"/> (Story 39-2 AC3/AC4/AC5).
/// The registered-type count is pinned; the per-type contract checks are LIVE now
/// (they iterate <see cref="DocumentTypeRegistry.All"/>, which is empty at 39-2
/// and bites when 39-3/39-4 land); the fail-loud resolution + <c>BuildIndex</c>
/// guards are exercised via small local fakes.
/// </summary>
[TestFixture]
public class DocumentTypeRegistryTests
{
    // -----------------------------------------------------------------------
    // (a) count pin
    // -----------------------------------------------------------------------

    [Test]
    public void All_registered_types_count_is_pinned_at_zero()
    {
        // 39-2 ships the vocabulary (DocumentTypeKey, 10 members) but ZERO
        // IDocumentType implementations — consciously (Design Decision D3). The
        // implementations arrive later and bump this pin:
        //   Story 39-3 registers +4  (0 -> 4)
        //   Story 39-4 registers +6  (4 -> 10, matching the DocumentTypeKey count)
        // Same posture as RolePhaseMapTests' HaveCount(79): the number moving is a
        // conscious, reviewed edit here, never an accident.
        DocumentTypeRegistry.All.Should().HaveCount(0);
    }

    // -----------------------------------------------------------------------
    // (b) per-registered-type contract loop — LIVE, empty today, bites at 39-3
    // -----------------------------------------------------------------------

    [Test]
    public void Every_registered_type_has_a_vocabulary_key_that_is_unique()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in DocumentTypeRegistry.All)
        {
            // Key must be a vocabulary wire string (throws if not).
            var parsed = DocumentTypeKeyExtensions.Parse(type.Key);
            parsed.ToWire().Should().Be(type.Key);
            seen.Add(type.Key).Should().BeTrue($"key '{type.Key}' must be unique across registered types");
        }
    }

    [Test]
    public void Every_registered_type_renders_a_deterministic_non_empty_contract()
    {
        foreach (var type in DocumentTypeRegistry.All)
        {
            var first = type.RenderContract();
            first.Should().NotBeNullOrWhiteSpace();
            // Determinism: 39-16 diffs this in CI.
            type.RenderContract().Should().Be(first);
        }
    }

    [Test]
    public void Every_registered_type_has_at_least_one_valid_and_one_invalid_self_checking_example()
    {
        foreach (var type in DocumentTypeRegistry.All)
        {
            type.Examples.Should().Contain(e => e.IsValid, $"{type.Key} needs a valid example");
            type.Examples.Should().Contain(e => !e.IsValid, $"{type.Key} needs an invalid example");

            foreach (var example in type.Examples)
            {
                using var doc = JsonDocument.Parse(example.PayloadJson);
                var result = type.Validate(doc.RootElement);
                if (example.IsValid)
                    result.IsValid.Should().BeTrue($"valid example '{example.Name}' must pass {type.Key}.Validate");
                else
                    result.IsValid.Should().BeFalse($"invalid example '{example.Name}' must fail {type.Key}.Validate");
            }
        }
    }

    // -----------------------------------------------------------------------
    // (c) fail-loud resolution
    // -----------------------------------------------------------------------

    [Test]
    public void Resolve_unknown_wire_string_throws_unknown()
    {
        var act = () => DocumentTypeRegistry.Resolve("nope");
        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.UNKNOWN");
    }

    [Test]
    public void Resolve_valid_but_unimplemented_key_throws_not_registered()
    {
        var byString = () => DocumentTypeRegistry.Resolve("decomposition");
        byString.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.NOT_REGISTERED");

        var byEnum = () => DocumentTypeRegistry.Resolve(DocumentTypeKey.Decomposition);
        byEnum.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.NOT_REGISTERED");
    }

    // -----------------------------------------------------------------------
    // (d) BuildIndex fail-loud core, exercised with fakes
    // -----------------------------------------------------------------------

    [Test]
    public void BuildIndex_accepts_distinct_vocabulary_keys()
    {
        var index = DocumentTypeRegistry.BuildIndex(new IDocumentType[]
        {
            new FakeDocumentType("findings"),
            new FakeDocumentType("plan"),
        });

        index.Should().HaveCount(2);
        index.Should().ContainKey("findings").And.ContainKey("plan");
    }

    [Test]
    public void BuildIndex_throws_duplicate_key_on_collision()
    {
        var act = () => DocumentTypeRegistry.BuildIndex(new IDocumentType[]
        {
            new FakeDocumentType("findings"),
            new FakeDocumentType("findings"),
        });

        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.DUPLICATE_KEY");
    }

    [Test]
    public void BuildIndex_throws_key_not_in_vocabulary_for_bogus_key()
    {
        var act = () => DocumentTypeRegistry.BuildIndex(new IDocumentType[]
        {
            new FakeDocumentType("not-a-type"),
        });

        act.Should().Throw<TammaError>().Which.Code.Should().Be("DOCUMENT.TYPE.KEY_NOT_IN_VOCABULARY");
    }

    /// <summary>
    /// Minimal local fake standing in for a 39-3/39-4 <see cref="IDocumentType"/>.
    /// </summary>
    private sealed class FakeDocumentType : IDocumentType
    {
        public FakeDocumentType(string key) => Key = key;

        public string Key { get; }
        public int SchemaVersion => 1;
        public Type PayloadClrType => typeof(object);
        public DocumentValidationResult Validate(JsonElement payload) => DocumentValidationResult.Valid();
        public string RenderContract() => $"contract:{Key}";
        public IReadOnlyList<DocumentExample> Examples => Array.Empty<DocumentExample>();
    }
}
