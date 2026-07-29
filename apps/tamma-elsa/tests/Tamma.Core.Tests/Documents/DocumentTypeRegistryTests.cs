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
    public void All_registered_types_count_is_pinned()
    {
        // 39-2 shipped the vocabulary (DocumentTypeKey, 10 members) with ZERO
        // IDocumentType implementations. The implementations arrive in stages and
        // bump this pin consciously:
        //   Story 39-3 registers +4  (0 -> 4)  <-- DONE (Decomposition, Findings,
        //                                        AmbiguityAssessment, Clarification)
        //   Story 39-4 registers +6  (4 -> 10) <-- DONE (Plan, Design, Review,
        //                                        TriageDecision, Diagnosis, TestSpec)
        //   Story 41-1b registers +6 (10 -> 16) <-- DONE (AcceptanceCriteria,
        //                                        BacklogOrdering, SprintPlan, TestPlan,
        //                                        ThreatModel, UxSpec) — the vocabulary
        //                                        stays COMPLETE, matching the
        //                                        DocumentTypeKey member count.
        // Same posture as RolePhaseMapTests' HaveCount(79): the number moving is a
        // conscious, reviewed edit here, never an accident.
        DocumentTypeRegistry.All.Should().HaveCount(16);
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
                {
                    result.IsValid.Should().BeTrue($"valid example '{example.Name}' must pass {type.Key}.Validate");
                    example.ExpectedViolationCodes.Should().BeEmpty(
                        $"valid example '{example.Name}' declares no expected violation codes");
                }
                else
                {
                    result.IsValid.Should().BeFalse($"invalid example '{example.Name}' must fail {type.Key}.Validate");

                    // D9: an invalid example must emit EXACTLY its declared codes.
                    example.ExpectedViolationCodes.Should().NotBeEmpty(
                        $"invalid example '{example.Name}' must declare the codes it expects (D9)");
                    result.Violations.Select(v => v.Code).Should().BeEquivalentTo(
                        example.ExpectedViolationCodes,
                        $"invalid example '{example.Name}' must emit exactly its ExpectedViolationCodes");
                }
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
    public void Every_vocabulary_key_now_resolves_to_an_implementation()
    {
        // 39-4 completed the vocabulary — all 10 DocumentTypeKey members are now
        // registered, so no valid key hits the NOT_REGISTERED fail-loud path anymore
        // (that path stays for defense, but is unreachable via a real key).
        foreach (var key in Enum.GetValues<DocumentTypeKey>())
        {
            var resolve = () => DocumentTypeRegistry.Resolve(key);
            resolve.Should().NotThrow($"vocabulary key '{key.ToWire()}' must have a registered implementation");
        }
    }

    [Test]
    public void Registered_39_3_keys_resolve_to_their_implementations()
    {
        DocumentTypeRegistry.Resolve(DocumentTypeKey.Decomposition).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.Decomposition));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.Findings).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.Findings));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.AmbiguityAssessment).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.AmbiguityAssessment));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.Clarification).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.Clarification));
    }

    [Test]
    public void Registered_41_1b_keys_resolve_to_their_implementations()
    {
        DocumentTypeRegistry.Resolve(DocumentTypeKey.AcceptanceCriteria).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.AcceptanceCriteria));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.BacklogOrdering).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.BacklogOrdering));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.SprintPlan).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.SprintPlan));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.TestPlan).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.TestPlan));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.ThreatModel).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.ThreatModel));
        DocumentTypeRegistry.Resolve(DocumentTypeKey.UxSpec).PayloadClrType
            .Should().Be(typeof(Tamma.Core.Documents.Types.UxSpec));
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
