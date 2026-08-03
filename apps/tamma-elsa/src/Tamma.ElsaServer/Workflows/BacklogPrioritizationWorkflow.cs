using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Documents;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Story 41-3 — Backlog Prioritization &amp; Grooming: a THIN BINDING over
/// <see cref="DocumentLifecycleWorkflow"/> (<c>DefinitionId = "document-lifecycle"</c>) producing
/// a typed <see cref="Tamma.Core.Documents.Types.BacklogOrdering"/> from the
/// <c>(product_owner, prioritize-backlog)</c> produce cell: a TOTAL ORDER over the
/// caller-supplied item set, with a rationale plus value/effort estimate per item and no ties.
///
/// <para><b>Greenfield, not a migration (D1).</b> The cell exists in the taxonomy
/// (<c>AgentAction.PrioritizeBacklog</c>, <c>RolePhaseMap</c>) with a prompt file, but NO workflow
/// dispatched it before this one — so there is no legacy event family to preserve, no parser to
/// delete, and no byte-stability obligation. The definition id is deliberately
/// <c>backlog-prioritization</c>, not <c>backlog-ordering</c>, so it never reads as the
/// document-type wire.</para>
///
/// <para><b>Set-scoped lineage anchor (D2).</b> A <c>BacklogOrdering</c> is not about one issue,
/// but <c>DocumentInstance.IssueId</c> is required and is the store's ONLY read key. The
/// lifecycle is therefore anchored on <c>BacklogBindingHelper.BuildAnchor(repository,
/// backlogScope)</c> — deterministic, recomputable from inputs alone, and the SHARED contract
/// 41-6 and 41-4 call by name for their upstream reads. FILED to 39-11: the honest fix is a
/// by-type/by-repository read; the anchor is computed in exactly one place so that migration is a
/// helper-body change.</para>
///
/// <para><b>Ranking evidence rides BOTH findings anchors (D3 / story AC2).</b> The store has no
/// set query, so evidence is gathered by BOUNDED PER-ITEM reads inside one Elsa
/// <see cref="ForEach{T}"/> — not N unrolled fetch nodes. Each iteration performs THREE
/// fail-closed <see cref="FetchLatestAcceptedDocumentActivity"/> reads:
/// <c>("triage-decision", itemIssueId)</c> — <c>TriagePODecisionWorkflow</c>'s anchor;
/// <c>("findings", itemIssueId)</c> — <c>ResearchWorkflow</c>'s anchor; and
/// <c>("findings", ScopeIssueId(itemIssueId, "triage-context"))</c> —
/// <c>TriageContextGatheringWorkflow</c>'s anchor. Reading only the bare id misses the
/// triage-context findings entirely AND silently returns a different workflow's document under
/// the same type key, which is the collision <c>ScopeIssueId</c> exists to prevent. Each hit is
/// appended to the DECLARED <c>evidence</c> carrier LABELLED WITH THE ANCHOR it came from, and
/// the accumulator is bounded so the composed value can never exceed
/// <c>PromptStoreService.MaxVariableValueLength</c> — over that, the renderer drops it as
/// unresolved and ships a literal <c>{{evidence}}</c>. Absence is never fatal: an item with no
/// upstream document is ranked from its title and summary.</para>
///
/// <para><b>The feedback carrier is a DECLARED variable (D4).</b>
/// <c>prioritize-backlog.md</c> declares <c>role, itemsJson, repoContext, evidence</c>; a producer
/// variable the front matter does not declare is silently dropped at render (the 39-15
/// render-drop lesson), so <c>feedbackVariableName = "evidence"</c> names a carrier the template
/// actually places in its body.</para>
///
/// <para><b>Resumable by design (D6).</b> <c>[ResumeBehavior(LatestStateReEntry)]</c> with a
/// <see cref="ComputeReEntryPositionActivity"/> node keyed on the D2 anchor and NO allowlist
/// entry: a thin binding owns no bookmark — the accept gate suspends inside the dispatched
/// <c>document-lifecycle</c> child, which this parent awaits with
/// <c>WaitForCompletion = true</c>. The re-entry gate covers the whole evidence-gather region and
/// the STARTED emission, so a re-entry neither re-reads N documents nor re-announces the run.
/// Zero <see cref="Finish"/>, zero <c>llm-call</c> dispatch, zero validate/retry plumbing.</para>
/// </summary>
[ResumeBehavior(ResumeMode.LatestStateReEntry)]
public class BacklogPrioritizationWorkflow : WorkflowBase
{
    private const string BacklogOrderingDocumentType = "backlog-ordering";
    private const string TriageDecisionDocumentType = "triage-decision";
    private const string FindingsDocumentType = "findings";

    /// <summary>
    /// <c>TriageContextGatheringWorkflow</c>'s producer scope — its lifecycle <c>issueId</c> is
    /// <c>CreationBindingHelper.ScopeIssueId(baseId, "triage-context")</c>, so its Findings are
    /// UNREACHABLE from a read at the bare item id (story Amendment A1).
    /// </summary>
    private const string TriageContextProducerScope = "triage-context";

    /// <summary>The DECLARED evidence/feedback carrier the rewritten cell places in its body (D4).</summary>
    private const string EvidenceVariableName = "evidence";
    private const string AmbiguityAssessmentDocumentType = "ambiguity-assessment";

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Backlog Prioritization";
        builder.DefinitionId = "backlog-prioritization";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Rank a candidate backlog item set into a typed BacklogOrdering (total order, rationale + value/effort per item, no ties) via the generic document lifecycle (produce → validate → review → revise → accept)";

        // ── Inputs ─────────────────────────────────────────────────────
        var sessionId    = builder.WithVariable<Guid>();
        var repository   = builder.WithVariable<string>("Repository", "");
        var backlogScope = builder.WithVariable<string>("BacklogScope", "");
        var itemsJson    = builder.WithVariable<string>("ItemsJson", "[]");
        var repoContext  = builder.WithVariable<string>("RepoContext", "");
        var tenantId     = builder.WithVariable<string>("TenantId", "");
        var acceptanceRulesJson = builder.WithVariable<string>("AcceptanceRulesJson", "");

        // ── D2 anchor + the parsed candidate set ───────────────────────
        var backlogAnchor     = builder.WithVariable<string>("BacklogAnchor", "");
        var evidenceAnchors   = builder.WithVariable<object>("EvidenceAnchors", new List<string>());
        var producerItemsJson = builder.WithVariable<string>("ProducerItemsJson", "[]");
        var itemCount         = builder.WithVariable<int>("ItemCount", 0);

        // ── D3 per-item evidence reads (bounded, fail-closed) ──────────
        var currentItemIssueId = builder.WithVariable<string>("CurrentItemIssueId", "");
        var evidence      = builder.WithVariable<string>("Evidence", "");
        var evidenceHits  = builder.WithVariable<int>("EvidenceHits", 0);

        var triageFound   = builder.WithVariable<bool>();
        var triageDocId   = builder.WithVariable<string>("TriageDocId", "");
        var triageJson    = builder.WithVariable<string>("TriageJson", "");
        var triageLineage = builder.WithVariable<string>();

        var researchFindingsFound   = builder.WithVariable<bool>();
        var researchFindingsDocId   = builder.WithVariable<string>("ResearchFindingsDocId", "");
        var researchFindingsJson    = builder.WithVariable<string>("ResearchFindingsJson", "");
        var researchFindingsLineage = builder.WithVariable<string>();

        var triageContextFindingsFound   = builder.WithVariable<bool>();
        var triageContextFindingsDocId   = builder.WithVariable<string>("TriageContextFindingsDocId", "");
        var triageContextFindingsJson    = builder.WithVariable<string>("TriageContextFindingsJson", "");
        var triageContextFindingsLineage = builder.WithVariable<string>();

        // ── Story 39-25 — threaded ambiguity score (leg 1) ─────────────
        var assessmentFound = builder.WithVariable<bool>();
        var assessmentJson  = builder.WithVariable<string>("AssessmentJson", "{}");

        // ── 39-10 re-entry position (D6) ───────────────────────────────
        var reEntryPositionJson = builder.WithVariable<string>();
        var reEntryDocJson  = builder.WithVariable<string>();
        var positionStage   = builder.WithVariable<string>("PositionStage", "produce");

        // ── Dispatched-workflow result + typed exit ────────────────────
        var lifecycleResult   = builder.WithVariable<IDictionary<string, object>?>();
        var lifecycleAccepted = builder.WithVariable<bool>();
        var lifecycleOrdered  = builder.WithVariable<bool>();
        var exitOutcome   = builder.WithVariable<string>("ExitOutcome", "");
        var exitDocId     = builder.WithVariable<string>("ExitDocId", "");
        var orderingJson  = builder.WithVariable<string>("OrderingJson", "[]");
        var orderedCount  = builder.WithVariable<int>("OrderedCount", 0);
        var failureDetail = builder.WithVariable<string>("FailureDetail", "");
        var outputStatus  = builder.WithVariable<string>();

        // ── Step 1: Read inputs, build the D2 anchor, parse the item set ──
        var readInputs = new SetVariable
        {
            Id = "ReadInputs", Name = "Read Inputs",
            Variable = backlogAnchor,
            Value = new(ctx =>
            {
                var sid = ctx.GetInput<Guid>("sessionId");
                if (sid == Guid.Empty) sid = Guid.NewGuid();
                sessionId.Set(ctx, sid);

                var repo = ctx.GetInput<string>("repository") ?? "";
                var scope = ctx.GetInput<string>("backlogScope") ?? "";
                var items = ctx.GetInput<string>("itemsJson") ?? "[]";
                repository.Set(ctx, repo);
                backlogScope.Set(ctx, scope);
                itemsJson.Set(ctx, items);
                repoContext.Set(ctx, ctx.GetInput<string>("repoContext") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                acceptanceRulesJson.Set(ctx, ctx.GetInput<string>("acceptanceRulesJson") ?? "");

                // D3 — the candidate set is parsed ONCE, bounded by MaxEvidenceReads. The
                // anchorable subset drives the evidence ForEach; the non-anchorable items are
                // recorded as explicit MISSES so "could not look" stays distinguishable from
                // "looked and found nothing" (story AC2).
                var parsed = BacklogBindingHelper.ParseItems(items, repo);
                itemCount.Set(ctx, parsed.Count);
                producerItemsJson.Set(ctx, BacklogBindingHelper.BuildItemsForProducer(parsed));
                evidenceAnchors.Set(ctx, (object)BacklogBindingHelper.SelectEvidenceAnchors(parsed).ToList());
                evidence.Set(ctx, BacklogBindingHelper.SeedEvidence(parsed));

                // D2 — the set-scoped lineage anchor. This IS the lifecycle's issue id.
                return (object)BacklogBindingHelper.BuildAnchor(repo, scope);
            })
        };
        readInputs.SetDisplayText("Read Inputs");

        // ── Step 2: Compute 39-10 re-entry position (D6), keyed on the D2 anchor ──
        var computeReEntry = new ComputeReEntryPositionActivity
        {
            Id = "ComputeReEntryPosition", Name = "Compute Re-Entry Position",
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            DocumentType = new(BacklogOrderingDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            CorrelationId = new(ctx => backlogAnchor.Get(ctx)),
            PositionJson = new(reEntryPositionJson),
            ExistingDocumentJson = new(reEntryDocJson),
        };
        computeReEntry.SetDisplayText("Compute Re-Entry Position");

        var readPositionStage = new SetVariable
        {
            Id = "ReadPositionStage", Name = "Read Position Stage",
            Variable = positionStage,
            Value = new(ctx =>
            {
                var position = DocumentLifecycleHelper.DeserializeReEntryPosition(reEntryPositionJson.Get(ctx));
                return position?.ResumeAt switch
                {
                    LifecycleResumeStage.Complete => "complete",
                    LifecycleResumeStage.Accept => "accept",
                    LifecycleResumeStage.Review => "review",
                    _ => "produce",
                };
            })
        };
        readPositionStage.SetDisplayText("Read Position Stage");

        // ── Step 3: FreshRun gate — a re-entry re-reads NO evidence and re-announces nothing (D6) ──
        var freshRun = new FlowDecision(ctx => positionStage.Get(ctx) == "produce")
        { Id = "FreshRun", Name = "Fresh Run?" };
        freshRun.SetDisplayText("Fresh Run?");

        var emitStarted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitGroomingStarted", Name = "Emit Grooming Started",
            EventType = new(BacklogEvents.Started),
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => backlogAnchor.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Detail = new(ctx => $"{itemCount.Get(ctx)} candidate items in scope '{backlogScope.Get(ctx)}'"),
            DataJson = new(ctx => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["itemCount"] = itemCount.Get(ctx),
                ["backlogScope"] = backlogScope.Get(ctx) ?? "",
            })),
        };
        emitStarted.SetDisplayText("Emit Grooming Started");

        // ── Step 4: the bounded evidence region (D3) ───────────────────
        // ONE ForEach over the anchorable item ids — NOT N compiled fetch nodes (which would be
        // unmaintainable and would distort the drift gate's dispatch-pair count).
        var readCurrentItem = new SetVariable
        {
            Id = "ReadCurrentItem", Name = "Read Current Item",
            Variable = currentItemIssueId,
            Value = new(ctx => (object)(ctx.GetVariable<string>("CurrentValue") ?? "")),
        };
        readCurrentItem.SetDisplayText("Read Current Item");

        // (a) TriagePODecisionWorkflow's anchor: the BARE item id.
        var fetchItemTriageDecision = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchItemTriageDecision", Name = "Fetch Item Triage Decision",
            IssueId = new(ctx => currentItemIssueId.Get(ctx)),
            DocumentTypeKey = new(TriageDecisionDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(triageFound),
            DocumentId = new(triageDocId),
            DocumentJson = new(triageJson),
            LineageJson = new(triageLineage),
        };
        fetchItemTriageDecision.SetDisplayText("Fetch Item Triage Decision");

        // (b) ResearchWorkflow's anchor: the BARE item id.
        var fetchItemResearchFindings = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchItemResearchFindings", Name = "Fetch Item Research Findings",
            IssueId = new(ctx => currentItemIssueId.Get(ctx)),
            DocumentTypeKey = new(FindingsDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(researchFindingsFound),
            DocumentId = new(researchFindingsDocId),
            DocumentJson = new(researchFindingsJson),
            LineageJson = new(researchFindingsLineage),
        };
        fetchItemResearchFindings.SetDisplayText("Fetch Item Research Findings");

        // (c) TriageContextGatheringWorkflow's anchor: the SCOPED id. Story Amendment A1 — the
        //     read that (b) structurally cannot perform.
        var fetchItemTriageContextFindings = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchItemTriageContextFindings", Name = "Fetch Item Triage-Context Findings",
            IssueId = new(ctx => CreationBindingHelper.ScopeIssueId(
                currentItemIssueId.Get(ctx), TriageContextProducerScope)),
            DocumentTypeKey = new(FindingsDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(triageContextFindingsFound),
            DocumentId = new(triageContextFindingsDocId),
            DocumentJson = new(triageContextFindingsJson),
            LineageJson = new(triageContextFindingsLineage),
        };
        fetchItemTriageContextFindings.SetDisplayText("Fetch Item Triage-Context Findings");

        var appendItemEvidence = new SetVariable
        {
            Id = "AppendItemEvidence", Name = "Append Item Evidence",
            Variable = evidence,
            Value = new(ctx =>
            {
                var item = currentItemIssueId.Get(ctx) ?? "";
                var scoped = CreationBindingHelper.ScopeIssueId(item, TriageContextProducerScope);
                var acc = evidence.Get(ctx) ?? "";
                var hits = evidenceHits.Get(ctx);

                if (triageFound.Get(ctx))
                {
                    acc = BacklogBindingHelper.AppendEvidence(
                        acc, item, TriageDecisionDocumentType, triageJson.Get(ctx));
                    hits++;
                }
                if (researchFindingsFound.Get(ctx))
                {
                    acc = BacklogBindingHelper.AppendEvidence(
                        acc, item, FindingsDocumentType, researchFindingsJson.Get(ctx));
                    hits++;
                }
                if (triageContextFindingsFound.Get(ctx))
                {
                    acc = BacklogBindingHelper.AppendEvidence(
                        acc, scoped, FindingsDocumentType, triageContextFindingsJson.Get(ctx));
                    hits++;
                }

                evidenceHits.Set(ctx, hits);
                return (object)acc;
            })
        };
        appendItemEvidence.SetDisplayText("Append Item Evidence");

        var gatherEvidence = new ForEach<string>
        {
            Id = "GatherEvidence", Name = "Gather Ranking Evidence",
            Items = new(ctx =>
            {
                var anchors = evidenceAnchors.Get(ctx);
                return anchors as ICollection<string> ?? new List<string>();
            }),
            Body = WithLabel(new Sequence
            {
                Id = "EvidenceIterationBody", Name = "Evidence Iteration",
                Activities =
                {
                    readCurrentItem,
                    fetchItemTriageDecision,
                    fetchItemResearchFindings,
                    fetchItemTriageContextFindings,
                    appendItemEvidence,
                }
            }, "Evidence Iteration"),
        };
        gatherEvidence.SetDisplayText("Gather Ranking Evidence");

        // ── Story 39-25 (leg 1): fetch the latest ACCEPTED ambiguity-assessment ──
        // Fail-closed: no accepted assessment for this run's anchor ⇒ Found=false ⇒ the
        // ambiguityScore dispatch key below is OMITTED (never a fabricated 0.0).
        // Run-scoped, keyed on the D2 set anchor (a BacklogOrdering has no single issue);
        // OUTSIDE the bounded per-item evidence loop. Honest null in practice — no assessment
        // is ever persisted under a backlog anchor today; the read stays fail-closed.
        var fetchAmbiguityAssessment = new FetchLatestAcceptedDocumentActivity
        {
            Id = "FetchAmbiguityAssessment", Name = "Fetch Accepted Ambiguity Assessment",
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            DocumentTypeKey = new(AmbiguityAssessmentDocumentType),
            TenantId = new(ctx => tenantId.Get(ctx)),
            Found = new(assessmentFound),
            DocumentJson = new(assessmentJson),
        };
        fetchAmbiguityAssessment.SetDisplayText("Fetch Accepted Ambiguity Assessment");

        // ── Step 5: Dispatch the generic document lifecycle ────────────
        var dispatchLifecycle = new DispatchWorkflow
        {
            Id = "DispatchLifecycle", Name = "Dispatch Document Lifecycle",
            WorkflowDefinitionId = new("document-lifecycle"),
            Input = new(ctx =>
            {
                var input = new Dictionary<string, object>
                {
                    ["documentType"]          = BacklogOrderingDocumentType,
                    ["producerRole"]          = AgentRole.ProductOwner.ToWire(),
                    ["producerAction"]        = AgentAction.PrioritizeBacklog.ToWire(),
                    ["producerVariablesJson"] = JsonSerializer.Serialize(new Dictionary<string, object>
                    {
                        // Every key below is DECLARED by prioritize-backlog.md's front matter and
                        // PLACED in its body — an undeclared key is dropped at render, and a declared
                        // key with no {{placeholder}} is a no-op (story AC7c).
                        ["itemsJson"]   = producerItemsJson.Get(ctx) ?? "[]",
                        ["repoContext"] = repoContext.Get(ctx) ?? "",
                        ["evidence"]    = evidence.Get(ctx) ?? "",
                    }),
                    // 39-6 D11 / D4 — repair/revise notes land in the DECLARED carrier.
                    ["feedbackVariableName"] = EvidenceVariableName,
                    ["sessionId"]            = sessionId.Get(ctx),
                    // D2 — the set-scoped anchor IS the lifecycle's issue id; a BacklogOrdering has
                    // no real issue to hang on and the store has no other read key.
                    ["issueId"]              = backlogAnchor.Get(ctx) ?? "",
                    ["correlationId"]        = backlogAnchor.Get(ctx) ?? "",
                    ["repository"]           = repository.Get(ctx) ?? "",
                    ["tenantId"]             = tenantId.Get(ctx) ?? "",
                    ["acceptanceRulesJson"]  = acceptanceRulesJson.Get(ctx) ?? "",
                };
                // 39-25 — thread the accepted assessment's score; ABSENT when none (null stays null).
                if (LifecycleBindingHelper.TryReadAssessmentScore(
                        assessmentFound.Get(ctx), assessmentJson.Get(ctx)) is double ambiguityScore)
                    input["ambiguityScore"] = ambiguityScore;
                return input;
            }),
            WaitForCompletion = new(true),
            Result = new(lifecycleResult),
        };
        dispatchLifecycle.SetDisplayText("Dispatch Document Lifecycle");

        // ── Step 6: Read the typed lifecycle exit (fail-closed) ────────
        var readLifecycleExit = new SetVariable
        {
            Id = "ReadLifecycleExit", Name = "Read Lifecycle Exit",
            Variable = orderingJson,
            Value = new(ctx =>
            {
                var exit = LifecycleBindingHelper.ReadLifecycleResult(lifecycleResult.Get(ctx));
                var accepted = LifecycleBindingHelper.IsAccepted(exit);

                lifecycleAccepted.Set(ctx, accepted);
                lifecycleOrdered.Set(ctx, !string.IsNullOrWhiteSpace(exit.DocumentId));
                exitOutcome.Set(ctx, exit.Outcome ?? "");
                exitDocId.Set(ctx, exit.DocumentId ?? "");
                outputStatus.Set(ctx, accepted ? "completed" : exit.Status);
                failureDetail.Set(ctx, BacklogBindingHelper.BuildFailureDetail(exit));
                orderedCount.Set(ctx, BacklogBindingHelper.CountOrderedItems(exit.DocumentJson));

                // The accepted ordering's items array raw text — the exact projection 41-6 reads.
                return accepted
                    ? BacklogBindingHelper.ProjectOrdering(exit.DocumentJson)
                    : "[]";
            })
        };
        readLifecycleExit.SetDisplayText("Read Lifecycle Exit");

        // ── Step 7: routing (typed values only) ────────────────────────
        var orderedGate = new FlowDecision(ctx => lifecycleOrdered.Get(ctx))
        { Id = "OrderingDrafted", Name = "Ordered?" };
        orderedGate.SetDisplayText("Ordered?");

        var acceptedGate = new FlowDecision(ctx => lifecycleAccepted.Get(ctx))
        { Id = "LifecycleAccepted", Name = "Accepted?" };
        acceptedGate.SetDisplayText("Accepted?");

        var emitOrdered = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitGroomingOrdered", Name = "Emit Grooming Ordered",
            EventType = new(BacklogEvents.Ordered),
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => backlogAnchor.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"{orderedCount.Get(ctx)} items ranked"),
            DataJson = new(ctx => JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["itemCount"] = itemCount.Get(ctx),
                ["evidenceHits"] = evidenceHits.Get(ctx),
                ["backlogScope"] = backlogScope.Get(ctx) ?? "",
            })),
        };
        emitOrdered.SetDisplayText("Emit Grooming Ordered");

        var emitAccepted = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitGroomingAccepted", Name = "Emit Grooming Accepted",
            EventType = new(BacklogEvents.Accepted),
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => backlogAnchor.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => $"{orderedCount.Get(ctx)} items accepted"),
        };
        emitAccepted.SetDisplayText("Emit Grooming Accepted");

        var emitFailed = new EmitDomainLifecycleEventActivity
        {
            Id = "EmitGroomingFailed", Name = "Emit Grooming Failed",
            EventType = new(BacklogEvents.Failed),
            IssueId = new(ctx => backlogAnchor.Get(ctx)),
            Repository = new(ctx => repository.Get(ctx)),
            CorrelationId = new(ctx => backlogAnchor.Get(ctx)),
            TenantId = new(ctx => tenantId.Get(ctx)),
            DocumentId = new(ctx => exitDocId.Get(ctx)),
            Detail = new(ctx => failureDetail.Get(ctx)),
        };
        emitFailed.SetDisplayText("Emit Grooming Failed");

        // ── Step 8: Expose output — the single terminal region ─────────
        var exposeOutput = new Sequence
        {
            Id = "ExposeOutput", Name = "Expose Output",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutputSessionId", Name = "Output Session Id", OutputName = new("sessionId"), OutputValue = new(ctx => (object)sessionId.Get(ctx).ToString()) }, "Output Session Id"),
                WithLabel(new SetOutput { Id = "OutputStatus", Name = "Output Status", OutputName = new("status"), OutputValue = new(ctx => (object)(outputStatus.Get(ctx) ?? "")) }, "Output Status"),
                WithLabel(new SetOutput { Id = "OutputOutcome", Name = "Output Outcome", OutputName = new("outcome"), OutputValue = new(ctx => (object)(exitOutcome.Get(ctx) ?? "")) }, "Output Outcome"),
                WithLabel(new SetOutput { Id = "OutputDocumentId", Name = "Output Document Id", OutputName = new("documentId"), OutputValue = new(ctx => (object)(exitDocId.Get(ctx) ?? "")) }, "Output Document Id"),
                WithLabel(new SetOutput { Id = "OutputOrdering", Name = "Output Ordering", OutputName = new("orderingJson"), OutputValue = new(ctx => (object)(orderingJson.Get(ctx) ?? "[]")) }, "Output Ordering"),
                // D2 — the anchor is an OUTPUT so a caller (and 41-6/41-4) can see the exact
                // string the ordering was written under without re-deriving it.
                WithLabel(new SetOutput { Id = "OutputBacklogAnchor", Name = "Output Backlog Anchor", OutputName = new("backlogAnchor"), OutputValue = new(ctx => (object)(backlogAnchor.Get(ctx) ?? "")) }, "Output Backlog Anchor"),
                WithLabel(new SetOutput { Id = "OutputError", Name = "Output Error", OutputName = new("error"), OutputValue = new(ctx => (object)(lifecycleAccepted.Get(ctx) ? "" : failureDetail.Get(ctx) ?? "")) }, "Output Error"),
            }
        };
        exposeOutput.SetDisplayText("Expose Output");

        // ── Build the flowchart ────────────────────────────────────────
        builder.Root = new Flowchart
        {
            Id = "BacklogPrioritizationFlowchart",
            Name = "Backlog Prioritization Flowchart",
            Start = readInputs,
            Activities =
            {
                readInputs, computeReEntry, readPositionStage, freshRun,
                emitStarted, gatherEvidence, fetchAmbiguityAssessment,
                dispatchLifecycle, readLifecycleExit,
                orderedGate, emitOrdered, acceptedGate, emitAccepted, emitFailed,
                exposeOutput,
            },
            Connections =
            {
                new(readInputs, computeReEntry),
                new(computeReEntry, readPositionStage),
                new(readPositionStage, freshRun),

                new(new FlowEndpoint(freshRun, "True"),  new FlowEndpoint(emitStarted)),
                new(emitStarted, gatherEvidence),
                // 39-25 — the ambiguity fetch (OUTSIDE the bounded loop, run-scoped) is the
                // single predecessor of the dispatch on both the fresh and re-entry paths.
                new(gatherEvidence, fetchAmbiguityAssessment),
                new(new FlowEndpoint(freshRun, "False"), new FlowEndpoint(fetchAmbiguityAssessment)),
                new(fetchAmbiguityAssessment, dispatchLifecycle),

                new(dispatchLifecycle, readLifecycleExit),
                new(readLifecycleExit, orderedGate),

                new(new FlowEndpoint(orderedGate, "True"),  new FlowEndpoint(emitOrdered)),
                new(emitOrdered, acceptedGate),
                new(new FlowEndpoint(orderedGate, "False"), new FlowEndpoint(acceptedGate)),

                new(new FlowEndpoint(acceptedGate, "True"),  new FlowEndpoint(emitAccepted)),
                new(emitAccepted, exposeOutput),
                new(new FlowEndpoint(acceptedGate, "False"), new FlowEndpoint(emitFailed)),
                new(emitFailed, exposeOutput),
            }
        };
    }
}
