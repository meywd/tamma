using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Core.Documents;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Template WORKED-EXAMPLE ↔ document-type conformance gate.
///
/// <para><b>Why this exists:</b> <see cref="ContractBindingTests"/> pins that a bound
/// cell's template literally CONTAINS the JSON field tokens its validator slices —
/// but a template can carry every token while its own worked example instructs the
/// WRONG document shape. That is not hypothetical: the shipped
/// <c>Prompts/architect/plan-system-design.md</c> instructed
/// <c>files: [{path, action}]</c> + <c>dependencies</c> while
/// <c>Tamma.Core/Documents/Types/Plan.cs</c> deserializes <c>files</c> as
/// <c>string[]</c> and reads <c>dependsOn</c> — every produce through the cell was a
/// guaranteed <c>MALFORMED_PAYLOAD</c>, and every existing test stayed green (the
/// tokens <c>"tasks"</c>/<c>"files"</c> were present). This fixture closes that gap:
/// for every DocumentType-bound cell it EXTRACTS the template's fenced JSON example
/// and runs it through the bound type's REAL <c>Validate()</c> (via
/// <see cref="DocumentTypeRegistry"/>).</para>
///
/// <para><b>Extraction mirrors the runtime ingest.</b> The shipped templates format
/// their instructed reply as a single fenced <c>```json</c> block. The extractor
/// takes the LAST such fence and then applies the exact carve the lifecycle applies
/// to a real reply — first <c>{</c> … last <c>}</c>, must parse
/// (<c>DocumentLifecycleWorkflow.ExtractJsonObject</c>). A bound template whose
/// fence holds no carvable JSON object is therefore a violation in itself: the
/// reply shape it instructs could never even be INGESTED, let alone validate.</para>
///
/// <para><b>Closed-set placeholders are normalized, not failed.</b> The repo's
/// template/RenderContract idiom writes closed vocabularies inline as
/// <c>"low|medium|high"</c> (or <c>"urgent | high | normal | low"</c>) string
/// values. Those are placeholder notation, not a wrong shape — and the 39-16
/// regeneration source (<c>IDocumentType.RenderContract</c>) uses the same idiom —
/// so before validation every string value of the form <c>a|b|c</c> is replaced by
/// its FIRST alternative. Structural drift (wrong field names, objects where
/// strings belong, missing required members, dangling ids) still fails.</para>
///
/// <para><b>Known pre-existing non-conformance</b> is baselined in
/// <see cref="KnownNonConformingTemplates"/> — the same ratchet shape as
/// <c>ContractBindingTests.KnownContractViolations</c>: entries may only ever be
/// REMOVED (count-pinned), and a stale entry (one whose template now conforms)
/// fails the build. Every entry is an UNBOUND cell owned by an Epic 41 story; a
/// BOUND cell may never be baselined — binding a cell (the 39-12+ lifecycle
/// migration) requires rewriting its template to conform (the 39-15 D7 precedent)
/// and deleting the baseline entry in the same change.</para>
/// </summary>
[TestFixture]
public class TemplateExampleConformanceTests
{
    // ====================================================================
    // Known non-conforming templates — a RATCHET, not an escape hatch
    // ====================================================================

    /// <summary>
    /// One baselined cell: the document type its Epic 41 owner will bind it to
    /// (<paramref name="IntendedDocumentTypeKey"/> — a registered wire key when the
    /// type exists today, else one of <see cref="PlannedFutureTypeKeys"/>), the
    /// owning story, and why the shipped template does not conform today.
    /// </summary>
    private sealed record BaselineEntry(string IntendedDocumentTypeKey, string OwningStory, string Reason);

    /// <summary>
    /// Cells whose shipped template ALREADY fails example-conformance (discovered
    /// while authoring this test — the Epic 41 planning pass's list, verified).
    /// All are UNBOUND today; each is owned by the Epic 41 story that will bind it.
    /// Baselining keeps the build green while making the debt explicit and
    /// un-growable: (a) any NEW violation on a bound cell still fails, (b) an entry
    /// whose template now conforms goes STALE and fails until deleted, (c) an entry
    /// whose cell gets BOUND fails until the binding story rewrites the template and
    /// deletes the entry. Entries may only ever be REMOVED, never added — the count
    /// pin below only goes DOWN.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string Role, string Action), BaselineEntry> KnownNonConformingTemplates =
        new Dictionary<(string, string), BaselineEntry>
        {
            [("architect", "plan-migration-strategy")] = new("plan", "41-12",
                "example instructs the legacy plan wire — files as {path, action} objects plus a " +
                "\"dependencies\" key — while Plan requires per-task files: string[] + dependsOn + testing " +
                "(MALFORMED_PAYLOAD on deserialization)"),
            [("tester", "plan-test-strategy")] = new("test-plan", "41-13",
                "example instructs the legacy plan wire; 41-1b mints the TestPlan document type this cell " +
                "will produce, and 41-13 rewrites the cell when it binds"),
            [("tester", "exploratory-test")] = new("findings", "41-14",
                "no JSON example at all — the template instructs file-format test output; 41-14 rewrites " +
                "the cell as a Findings (exploratory charter) producer"),
            [("tester", "write-regression-test")] = new("test-spec", "41-16",
                "no JSON example at all — the template instructs file-format test output; 41-16 rewrites " +
                "the cell as a TestSpec (bound regression case) producer"),
            [("tester", "verify-acceptance")] = new("review", "41-15",
                "example instructs an {issues, summary: {decision, ...}} shape; Review requires root-level " +
                "subject/decision/summary (summary is a string, not an object) — MALFORMED_PAYLOAD"),
            [("product_owner", "define-acceptance-criteria")] = new("acceptance-criteria", "41-2",
                "example instructs the legacy plan wire; 41-1b mints the AcceptanceCriteria document type " +
                "this cell will produce, and 41-2 rewrites the cell when it binds"),
            [("product_owner", "plan-roadmap")] = new("prose", "41-4",
                "example instructs the legacy plan wire; 41-4 produces prose (roadmap, audience=stakeholder) " +
                "once 41-1c lands the prose document type"),
            [("product_owner", "prioritize-backlog")] = new("backlog-ordering", "41-3",
                "example instructs the retired P0-P3 / severity / ownerRole triage vocabulary; 41-1b mints " +
                "the BacklogOrdering document type this cell will produce"),
            [("devops", "plan-incident-response")] = new("plan", "41-22",
                "example instructs the legacy plan wire — files as {path, action} objects plus a " +
                "\"dependencies\" key — MALFORMED_PAYLOAD against Plan"),
            [("devops", "write-postmortem")] = new("prose", "41-22",
                "no JSON example — the template instructs a markdown issue-comment format; becomes " +
                "prose (postmortem, audience=engineering) once 41-1c lands"),
            [("tech_writer", "update-changelog")] = new("prose", "41-24",
                "no JSON example — the template instructs a markdown issue-comment format; becomes " +
                "prose (release-notes/changelog) once 41-1c lands"),
        };

    /// <summary>
    /// The ratchet's count pin. This number may only ever DECREASE — remove the
    /// baseline entry (and decrement this) when the owning story rewrites its
    /// template; never add entries.
    /// </summary>
    private const int KnownNonConformingTemplateCount = 11;

    /// <summary>
    /// The document-type keys Epic 41 plans to mint (41-1b: TestPlan,
    /// AcceptanceCriteria, BacklogOrdering; 41-1c: prose). A baseline entry whose
    /// intended key is not registered in <see cref="DocumentTypeRegistry"/> must
    /// name one of these — anything else is a typo. The moment one of these keys IS
    /// registered, its entries start being staleness-checked against the real
    /// validator automatically.
    /// </summary>
    private static readonly IReadOnlySet<string> PlannedFutureTypeKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "test-plan", "acceptance-criteria", "backlog-ordering", "prose",
    };

    // ====================================================================
    // Extraction — mirrors the runtime ingest path
    // ====================================================================

    /// <summary>The template idiom: the instructed reply shape is a fenced ```json block.</summary>
    private static readonly Regex JsonFence = new("```json\\s*\\n(.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// A closed-set placeholder value: <c>a|b|c</c> (optionally spaced, as in
    /// <c>urgent | high | normal | low</c>) — the RenderContract idiom for "one of".
    /// </summary>
    private static readonly Regex ClosedSetPlaceholder = new(@"^\s*[A-Za-z0-9_.\-]+(\s*\|\s*[A-Za-z0-9_.\-]+)+\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Extract the worked example from a template body: take the LAST ```json fence
    /// (the instructed output shape), then apply the exact carve the runtime applies
    /// to a reply — first <c>{</c> … last <c>}</c>, must parse
    /// (<c>DocumentLifecycleWorkflow.ExtractJsonObject</c>). Returns the parsed
    /// object, or a failure reason naming what is missing.
    /// </summary>
    internal static (JsonElement? Example, string? FailureReason) ExtractExample(string template)
    {
        var matches = JsonFence.Matches(template);
        if (matches.Count == 0)
            return (null, "the template has no ```json fenced example block");

        var body = matches[^1].Groups[1].Value;
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start)
            return (null, "the ```json fence contains no {…} JSON object — the lifecycle's ExtractJsonObject " +
                          "carve (first '{' … last '}') would reject the instructed reply outright, so a " +
                          "conforming reply cannot even be ingested");

        var candidate = body[start..(end + 1)];
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return (doc.RootElement.Clone(), null);
        }
        catch (JsonException e)
        {
            return (null, $"the fenced example does not parse as JSON ({e.Message})");
        }
    }

    /// <summary>
    /// Replace every closed-set placeholder string value (<c>"low|medium|high"</c>)
    /// with its first alternative, recursively. Everything else passes through
    /// verbatim, so structural drift still fails validation.
    /// </summary>
    internal static JsonElement NormalizeClosedSetPlaceholders(JsonElement example)
    {
        var normalized = NormalizeNode(JsonNode.Parse(example.GetRawText()));
        using var doc = JsonDocument.Parse(normalized?.ToJsonString() ?? "null");
        return doc.RootElement.Clone();
    }

    private static JsonNode? NormalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                var newObj = new JsonObject();
                foreach (var (key, value) in obj)
                    newObj[key] = NormalizeNode(value);
                return newObj;
            case JsonArray arr:
                var newArr = new JsonArray();
                foreach (var item in arr)
                    newArr.Add(NormalizeNode(item));
                return newArr;
            case JsonValue value when value.TryGetValue<string>(out var s) && ClosedSetPlaceholder.IsMatch(s):
                return JsonValue.Create(s.Split('|')[0].Trim());
            case null:
                return null;
            default:
                return node.DeepClone();
        }
    }

    // ====================================================================
    // Evaluation core
    // ====================================================================

    /// <summary>
    /// Resolve the registered <see cref="IDocumentType"/> a ContractBindingTests
    /// parser-authority string (<c>"PlanDocumentType.Validate"</c>) names.
    /// </summary>
    private static IDocumentType ResolveByValidatorAuthority(string parserAuthority)
    {
        var typeName = parserAuthority[..^".Validate".Length];
        var match = DocumentTypeRegistry.All.SingleOrDefault(t => t.GetType().Name == typeName);
        match.Should().NotBeNull(
            $"the binding authority '{parserAuthority}' names no registered IDocumentType — " +
            "DocumentTypeRegistry.All has no implementation whose CLR type is " + typeName);
        return match!;
    }

    /// <summary>
    /// Evaluate one cell against a document type. Returns <c>null</c> when the
    /// template's worked example CONFORMS (extractable + valid), else a description
    /// of the non-conformance. Uses the context-free <c>Validate</c> — cross-document
    /// rules (e.g. TestSpec's CASE_UNKNOWN_TASK_ID) need a consumed document and are
    /// out of scope for a shipped static example.
    /// </summary>
    private static string? EvaluateNonConformance(string role, string action, IDocumentType type)
    {
        var template = SystemPrompts.GetRoleAction(role, action);
        if (template is null)
            return "no shipped template exists for the cell";

        var (example, reason) = ExtractExample(template.Template);
        if (example is null)
            return reason;

        var result = type.Validate(NormalizeClosedSetPlaceholders(example.Value));
        if (result.IsValid)
            return null;

        return string.Join("; ", result.Violations.Select(v => $"{v.Code}: {v.Message}"));
    }

    // ====================================================================
    // Test 1 — every DocumentType-bound cell's worked example validates
    // ====================================================================

    [Test]
    public void EveryDocumentTypeBoundCell_ShippedExampleValidatesAgainstItsBoundType()
    {
        var boundCells = ContractBindingTests.DocumentTypeValidatedCells;
        boundCells.Should().NotBeEmpty(
            "the DocumentType-backed subset of ContractBindingTests.Bindings came back empty — " +
            "this gate would be a no-op (ContractBindingTests' universal pin should also be failing)");

        var violations = new List<string>();
        foreach (var ((role, action), parserAuthority) in boundCells.OrderBy(kv => kv.Key))
        {
            var type = ResolveByValidatorAuthority(parserAuthority);
            var nonConformance = EvaluateNonConformance(role, action, type);
            if (nonConformance is not null)
            {
                violations.Add(
                    $"  ({role}, {action}) → {type.GetType().Name}: {nonConformance}" + Environment.NewLine +
                    $"      fix Prompts/{role}/{action}.md so its worked example is a VALID '{type.Key}' document " +
                    "(mirror the type's RenderContract; the 39-15 D7 rewrite precedent). A bound cell may NOT be " +
                    "baselined in KnownNonConformingTemplates.");
            }
        }

        violations.Should().BeEmpty(
            "every DocumentType-bound prompt cell's shipped worked example must actually validate against the " +
            "document type its callers validate with — a template that instructs the wrong shape makes every " +
            "produce through the cell a guaranteed validation failure at runtime while all token-presence tests " +
            "stay green. Non-conforming templates:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    // ====================================================================
    // Test 2 — the ratchet: entries must still be non-conforming, and may only shrink
    // ====================================================================

    [Test]
    public void KnownNonConformingTemplates_AreStillNonConforming_AndCountOnlyShrinks()
    {
        KnownNonConformingTemplates.Should().HaveCount(KnownNonConformingTemplateCount,
            "the baseline is count-pinned; the pin may only ever DECREASE (delete an entry + decrement " +
            "the pin when its owning Epic 41 story rewrites the template) — never add entries");

        var problems = new List<string>();
        foreach (var ((role, action), entry) in KnownNonConformingTemplates.OrderBy(kv => kv.Key))
        {
            entry.Reason.Should().NotBeNullOrWhiteSpace("every baseline entry must say why it does not conform");
            entry.OwningStory.Should().NotBeNullOrWhiteSpace("every baseline entry must name its owning Epic 41 story");

            if (SystemPrompts.GetRoleAction(role, action) is null)
            {
                problems.Add($"  ({role}, {action}): baselined but no shipped template exists — the cell left the " +
                             "taxonomy; delete the entry");
                continue;
            }

            if (!TryResolveRegisteredType(entry.IntendedDocumentTypeKey, out var type))
            {
                // Intended type not registered yet — it must be one of the PLANNED Epic 41
                // keys (else it is a typo). Once 41-1b/41-1c registers the key, this entry
                // automatically starts being staleness-checked against the real validator.
                if (!PlannedFutureTypeKeys.Contains(entry.IntendedDocumentTypeKey))
                    problems.Add($"  ({role}, {action}): intended document type '{entry.IntendedDocumentTypeKey}' is " +
                                 "neither registered in DocumentTypeRegistry nor one of the planned Epic 41 keys " +
                                 $"({string.Join(", ", PlannedFutureTypeKeys.OrderBy(k => k))}) — fix the key");
                continue;
            }

            var nonConformance = EvaluateNonConformance(role, action, type!);
            if (nonConformance is null)
                problems.Add($"  ({role}, {action}): baselined as non-conforming but its example now VALIDATES " +
                             $"against '{type!.Key}' — delete its KnownNonConformingTemplates entry and decrement " +
                             "the count pin (the ratchet only turns one way)");
        }

        problems.Should().BeEmpty(
            "KnownNonConformingTemplates must list ONLY cells whose shipped template still fails " +
            "example-conformance:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static bool TryResolveRegisteredType(string key, out IDocumentType? type)
    {
        type = DocumentTypeRegistry.All.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.Ordinal));
        return type is not null;
    }

    // ====================================================================
    // Test 3 — baseline entries must be UNBOUND cells
    // ====================================================================

    [Test]
    public void KnownNonConformingTemplates_OnlyBaselineUnboundCells()
    {
        // A bound cell's example non-conformance is a live runtime defect (its produce
        // validates through the type TODAY) — it must be FIXED, never baselined. When an
        // Epic 41 story binds a baselined cell, the SAME change must rewrite the template
        // to conform (the thin-binding D7 rewrite precedent) and delete the entry here.
        var bound = ContractBindingTests.AllBoundCells.ToHashSet();
        var boundBaselined = KnownNonConformingTemplates.Keys
            .Where(bound.Contains)
            .Select(k => $"  ({k.Role}, {k.Action})")
            .ToList();

        boundBaselined.Should().BeEmpty(
            "every KnownNonConformingTemplates entry must be an UNBOUND cell — binding a cell requires " +
            "rewriting its template to conform and deleting its baseline entry in the same change:" +
            Environment.NewLine + string.Join(Environment.NewLine, boundBaselined));
    }

    // ====================================================================
    // Test 4 — extractor/normalizer behavior pins
    // ====================================================================

    [Test]
    public void Extractor_CarvesTheLastJsonFence_AndFailsLoudWithoutOne()
    {
        var template = """
            Some instructions.
            ```json
            {"first": true}
            ```
            More prose, then the instructed output shape:
            ```json
            {"tasks": [{"id": "T1"}]}
            ```
            """;
        var (example, reason) = ExtractExample(template);
        reason.Should().BeNull();
        example!.Value.TryGetProperty("tasks", out _).Should().BeTrue("the LAST fence is the instructed output shape");

        var (none, noneReason) = ExtractExample("no fenced example here");
        none.Should().BeNull();
        noneReason.Should().Contain("no ```json fenced example block");

        var (bareArray, arrayReason) = ExtractExample("```json\n[\"just\", \"strings\"]\n```");
        bareArray.Should().BeNull(
            "a bare array instructs a reply the lifecycle's first-'{'-to-last-'}' carve can never ingest");
        arrayReason.Should().Contain("no {…} JSON object");
    }

    [Test]
    public void Normalizer_ReplacesClosedSetPlaceholders_AndOnlyThose()
    {
        using var doc = JsonDocument.Parse("""
            {
              "severity": "low|medium|high",
              "priority": "urgent | high | normal | low",
              "url": "https://example.com/a",
              "path": "src/Foo.cs",
              "text": "either this or that",
              "nested": [{"type": "bug|feature"}],
              "count": 3
            }
            """);
        var normalized = NormalizeClosedSetPlaceholders(doc.RootElement);

        normalized.GetProperty("severity").GetString().Should().Be("low");
        normalized.GetProperty("priority").GetString().Should().Be("urgent");
        normalized.GetProperty("url").GetString().Should().Be("https://example.com/a", "URLs are not alternations");
        normalized.GetProperty("path").GetString().Should().Be("src/Foo.cs");
        normalized.GetProperty("text").GetString().Should().Be("either this or that", "prose with spaces is not an alternation");
        normalized.GetProperty("nested")[0].GetProperty("type").GetString().Should().Be("bug");
        normalized.GetProperty("count").GetInt32().Should().Be(3);
    }
}
