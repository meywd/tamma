namespace Tamma.Activities.Clarify;

/// <summary>
/// Story 3.5 (Clarifying Questions Workflow) — central catalogue of the
/// <c>CLARIFY.*</c> DCB event types emitted by the <c>clarifying-questions</c>
/// sub-workflow via <see cref="EmitClarifyEventActivity"/>. Type pattern follows
/// the platform's <c>AGGREGATE.ACTION.STATUS</c> convention (<c>CLAUDE.md</c>) and
/// mirrors the sibling event catalogues
/// (<see cref="Tamma.Activities.Blocker.BlockerEvents"/>,
/// <see cref="Tamma.Activities.ADL.BranchEvents"/>).
///
/// <para>The clarifying-questions workflow resolves ambiguity in an issue /
/// requirement: it asks the LLM (via the mediated <c>llm-call</c> path — the engine
/// holds no LLM credential) to generate clarifying questions, DELIVERS them to the
/// issue, SUSPENDS on a bookmark awaiting the human answers, then RESUMES and
/// incorporates the answers into a disambiguated requirement. Each transition is an
/// auditable step so time-travel debugging and the Epic-32 learning loop can
/// reconstruct WHAT was ambiguous, WHICH questions were asked, and HOW the answers
/// resolved it. Without these events the clarification is invisible to the audit
/// trail (Story 3.5 AC "System tracks question status" + "Question-answer pairs are
/// stored for future learning").</para>
///
/// <para>Events are emitted via <c>TammaEventEmitter.Emit</c> into the workflow's
/// <c>tamma:events</c> transient list and persisted <i>durably</i> by the engine
/// event drain (<c>EventPersistenceMiddleware</c> + <c>EventDrain</c>) — the same
/// pattern <see cref="Tamma.Activities.Blocker.EmitBlockerEventActivity"/> uses. No
/// activity holds a DB / repository dependency of its own; the drain resolves the
/// tenant from the workflow scope and each event carries a <c>tenantId</c> tag so
/// per-tenant data stays tenant-scoped.</para>
///
/// <list type="bullet">
///   <item><description><c>CLARIFY.QUESTIONS.GENERATED</c> — the LLM produced a
///     non-empty set of clarifying questions for the ambiguous requirement.</description></item>
///   <item><description><c>CLARIFY.QUESTIONS.DELIVERED</c> — the questions were
///     posted to the issue / surfaced to the stakeholder (carries channel).</description></item>
///   <item><description><c>CLARIFY.ANSWERS.RECEIVED</c> — the human answered; the
///     workflow resumed from its bookmark with the answers.</description></item>
///   <item><description><c>CLARIFY.REQUIREMENTS.CLARIFIED</c> — terminal success:
///     the answers were incorporated into a disambiguated requirement.</description></item>
///   <item><description><c>CLARIFY.QUESTIONS.FAILED</c> — LOUD (error-status): the
///     question-generation <c>llm-call</c> failed or returned unparseable output;
///     the workflow fails closed rather than proceeding with fabricated questions.</description></item>
///   <item><description><c>CLARIFY.INCORPORATION.FAILED</c> — LOUD (error-status):
///     the answer-incorporation <c>llm-call</c> failed or returned unparseable
///     output; fail-closed, never a fabricated clarification.</description></item>
///   <item><description><c>CLARIFY.ANSWERS.TIMED_OUT</c> — LOUD (error-status):
///     the answer SLA expired with no human response. A distinct, auditable
///     terminal, NEVER a silent false success.</description></item>
/// </list>
/// </summary>
public static class ClarifyEvents
{
    public const string QuestionsGenerated = "CLARIFY.QUESTIONS.GENERATED";
    public const string QuestionsDelivered = "CLARIFY.QUESTIONS.DELIVERED";
    public const string AnswersReceived = "CLARIFY.ANSWERS.RECEIVED";
    public const string RequirementsClarified = "CLARIFY.REQUIREMENTS.CLARIFIED";

    // LOUD (error-status) terminals — a fabricated / degraded outcome must never be
    // recorded as a false success.
    public const string QuestionsFailed = "CLARIFY.QUESTIONS.FAILED";
    public const string IncorporationFailed = "CLARIFY.INCORPORATION.FAILED";
    public const string AnswersTimedOut = "CLARIFY.ANSWERS.TIMED_OUT";

    /// <summary>
    /// Parse a tenant id from the loose string form threaded through the workflow
    /// inputs. Returns <c>null</c> for empty / single-user / unparseable values
    /// (clarify events in single-user mode are platform-scope, TenantId null).
    /// Mirrors <see cref="Tamma.Activities.Blocker.BlockerEvents.ParseTenantId"/>.
    /// </summary>
    public static Guid? ParseTenantId(string? tenantId)
        => Guid.TryParse(tenantId, out var g) ? g : null;

    /// <summary>
    /// Status convention: a failed generation / incorporation and an answer SLA
    /// expiry are LOUD (error-status) audit rows; every other transition
    /// (generated, delivered, answers received, requirements clarified) is a normal
    /// (success-status) row. Keeps a degraded/expired terminal from ever being
    /// recorded as a false success.
    /// </summary>
    public static string StatusForEvent(string type) => type switch
    {
        QuestionsFailed => "error",
        IncorporationFailed => "error",
        AnswersTimedOut => "error",
        _ => "success",
    };
}
