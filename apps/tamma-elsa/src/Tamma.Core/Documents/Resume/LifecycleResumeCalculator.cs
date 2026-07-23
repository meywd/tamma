namespace Tamma.Core.Documents.Resume;

/// <summary>
/// Story 39-10 (AC5, Design Decision D1) — the PURE, I/O-free re-entry
/// reconstruction core. Folds the 39-11 latest-accepted read plus the ordered
/// <c>DOCUMENT.*</c> / <c>APPROVAL.*</c> event slice for one issue+type into a typed
/// <see cref="LifecycleResumePosition"/>, in the left-fold style of
/// <c>ReplayReconstructor</c> (which stays the forensic full-replay fallback — this
/// is the hot read model, not a second replayer).
///
/// <para><b>It never guesses.</b> When the store and the stream disagree — an
/// accepted store row with no <c>DOCUMENT.ACCEPTED</c> event, or the converse — it
/// throws <c>DOCUMENT.REENTRY.INCONSISTENT_STATE</c> (fail-loud, pointing at the 4-8
/// replay surface) rather than picking a stage. Skipping a produce that was NOT
/// accepted is worse than not skipping, so ambiguity is an error.</para>
///
/// <para>The event-type strings are mirrored here as local constants because Core
/// cannot reference <c>Tamma.Activities</c> (where <c>DocumentEvents</c> /
/// <c>ApprovalEvents</c> live); they are stable wire strings on the
/// <c>AGGREGATE.ACTION.STATUS</c> convention.</para>
/// </summary>
public static class LifecycleResumeCalculator
{
    // Mirror of Tamma.Activities.Documents.DocumentEvents (39-6) — the DOCUMENT.* family read by re-entry.
    private const string ProducedSuccess = "DOCUMENT.PRODUCED.SUCCESS";
    private const string ValidatedSuccess = "DOCUMENT.VALIDATED.SUCCESS";
    private const string ValidatedFailed = "DOCUMENT.VALIDATED.FAILED";
    private const string Reviewed = "DOCUMENT.REVIEWED";
    private const string RevisionStarted = "DOCUMENT.REVISION_STARTED";
    private const string Accepted = "DOCUMENT.ACCEPTED";

    // Mirror of Tamma.Activities.Documents.ApprovalEvents (39-8) — the accept-gate session recovery.
    private const string ApprovalRequested = "APPROVAL.REQUESTED";
    private const string ApprovalProvided = "APPROVAL.PROVIDED";

    /// <summary>
    /// Reconstruct the resume position for <paramref name="documentTypeKey"/> from the
    /// latest-accepted read and the ordered (oldest-first) event slice.
    /// </summary>
    /// <param name="documentTypeKey">The document type being (re)produced.</param>
    /// <param name="latestAccepted">The 39-11 latest-accepted ref for this type, or null.</param>
    /// <param name="orderedEvents">The issue's <c>DOCUMENT.*</c>/<c>APPROVAL.*</c> events, oldest-first.</param>
    /// <exception cref="TammaError">Code <c>DOCUMENT.REENTRY.INCONSISTENT_STATE</c> on store/stream disagreement.</exception>
    public static LifecycleResumePosition Reconstruct(
        string documentTypeKey,
        AcceptedDocumentRef? latestAccepted,
        IReadOnlyList<ResumeEventRow> orderedEvents)
    {
        if (string.IsNullOrWhiteSpace(documentTypeKey))
            throw new ArgumentException("documentTypeKey is required for re-entry reconstruction.", nameof(documentTypeKey));

        // Only events for THIS document type matter. A row with an unset DocumentTypeKey
        // is treated as non-matching (fail-closed): the fold must not fold a foreign
        // type's transition into this type's position.
        var slice = orderedEvents
            .Where(e => string.Equals(e.DocumentTypeKey, documentTypeKey, StringComparison.Ordinal))
            .ToList();

        var hasAcceptedEvent = slice.Any(e => e.Type == Accepted);
        var storeAccepted = latestAccepted is not null;

        // Store/stream disagreement is a hard error — re-entry never guesses (D1).
        if (storeAccepted != hasAcceptedEvent)
            throw Inconsistent(documentTypeKey, storeAccepted, hasAcceptedEvent);

        if (storeAccepted)
        {
            return new LifecycleResumePosition
            {
                DocumentTypeKey = documentTypeKey,
                ResumeAt = LifecycleResumeStage.Complete,
                ExistingDocumentId = latestAccepted!.DocumentId,
                ExistingRevision = latestAccepted.Revision,
                Basis = $"{documentTypeKey} already accepted (revision {latestAccepted.Revision}); short-circuit to complete.",
            };
        }

        // ── Left fold over the ordered slice ──────────────────────────────
        Guid? latestDocId = null;
        int? latestRevision = null;
        var reviewReady = false;               // last validation was SUCCESS with no later REVIEWED/REVISION_STARTED
        Guid? pendingApprovalSession = null;   // unanswered APPROVAL.REQUESTED session

        foreach (var e in slice)
        {
            if (e.DocumentId is Guid id) latestDocId = id;
            if (e.Revision is int rev) latestRevision = rev;

            switch (e.Type)
            {
                case ValidatedSuccess:
                    reviewReady = true;
                    break;
                case ValidatedFailed:
                    reviewReady = false;
                    break;
                case Reviewed:
                case RevisionStarted:
                    // A landed review or a started revision means this draft is no
                    // longer "produced-but-unreviewed".
                    reviewReady = false;
                    break;
                case ApprovalRequested:
                    pendingApprovalSession = e.SessionId;
                    break;
                case ApprovalProvided:
                    pendingApprovalSession = null;
                    break;
                case ProducedSuccess:
                    // Validation follows; readiness is driven off the validation row.
                    break;
            }
        }

        // Precedence: Accept (latest reachable stage) > Review > Produce.
        if (pendingApprovalSession is Guid session && session != Guid.Empty)
        {
            return new LifecycleResumePosition
            {
                DocumentTypeKey = documentTypeKey,
                ResumeAt = LifecycleResumeStage.Accept,
                ExistingDocumentId = latestDocId,
                ExistingRevision = latestRevision,
                PendingDecisionSessionId = session,
                Basis = $"{documentTypeKey} awaiting an undecided acceptance (session {session}); re-suspend on the recovered gate.",
            };
        }

        if (reviewReady && latestDocId is Guid docId)
        {
            return new LifecycleResumePosition
            {
                DocumentTypeKey = documentTypeKey,
                ResumeAt = LifecycleResumeStage.Review,
                ExistingDocumentId = docId,
                ExistingRevision = latestRevision,
                Basis = $"{documentTypeKey} produced-but-unreviewed at revision {latestRevision ?? 0}; skip produce/validate and review it.",
            };
        }

        return LifecycleResumePosition.Fresh(
            documentTypeKey,
            slice.Count == 0
                ? $"No prior {documentTypeKey} lineage for this issue; run fresh."
                : $"No resumable {documentTypeKey} sub-stage (mid-repair / mid-revision / non-accepted terminal); run fresh.");
    }

    private static TammaError Inconsistent(string documentTypeKey, bool storeAccepted, bool streamAccepted) => new(
        "DOCUMENT.REENTRY.INCONSISTENT_STATE",
        $"Re-entry reconstruction found the document store and the DCB event stream disagree for " +
        $"type '{documentTypeKey}': store-accepted={storeAccepted}, stream-has-ACCEPTED={streamAccepted}. " +
        "Re-entry refuses to guess a resume position — reconcile via the Story 4-8 replay surface.",
        new Dictionary<string, object?>
        {
            ["documentTypeKey"] = documentTypeKey,
            ["storeAccepted"] = storeAccepted,
            ["streamAccepted"] = streamAccepted,
        },
        retryable: false,
        severity: TammaErrorSeverity.High);
}
