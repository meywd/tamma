using System.Text.Json;

namespace Tamma.Api.Dtos.Engine;

public record SendCommandRequest(string Command, object? Args);

/// <summary>
/// Audit finding 004 — restored the TS shape:
/// <c>{repository, issueNumber, findings | role+finding}</c>. The original
/// port reduced this to <c>{IssueNumber, Context}</c>, which never bound
/// any of the fields the deployed Elsa Context.Store* activities send.
/// </summary>
public record StoreContextRequest(
    string? Repository,
    int IssueNumber,
    JsonElement? Findings,
    string? Role,
    JsonElement? Finding);

/// <summary>
/// Audit finding 004 — restored TS shape:
/// <c>{repository, issueNumber, query, role?, maxTokens?}</c>.
/// </summary>
public record QueryContextRequest(
    string? Repository,
    int? IssueNumber,
    string Query,
    string? Role,
    int? MaxTokens);

/// <summary>
/// Audit finding 008 — field renamed from <c>Repo</c> to <c>Repository</c>
/// to match the deployed Elsa activity payloads (<c>repository</c>).
/// </summary>
public record IssueCommentRequest(string Repository, int IssueNumber, string Body);

/// <summary>
/// Audit finding 009 — field renamed from <c>Repo</c> to <c>Repository</c>
/// to match the deployed Elsa activity payloads.
/// </summary>
public record IssueLabelRequest(string Repository, int IssueNumber, string[] Labels);

/// <summary>
/// Audit finding 010 — field renamed from <c>Repo</c> to <c>Repository</c>;
/// added missing <c>Assignees</c> field from TS.
/// </summary>
public record CreateIssueRequest(
    string Repository,
    string Title,
    string? Body,
    string[]? Labels,
    string[]? Assignees);

/// <summary>
/// Audit finding 011 — restored TS shape:
/// <c>{repository, branchName, workflowFile, inputs?}</c>. The original
/// <c>{Repo, Ref, Workflow}</c> bound none of the deployed Elsa
/// <c>TriggerCIActivity</c> fields.
/// </summary>
public record TriggerCiRequest(
    string Repository,
    string BranchName,
    string WorkflowFile,
    Dictionary<string, string>? Inputs);

/// <summary>
/// Audit finding 001 — restored the TS shape that all 11 deployed Elsa
/// activities POST: <c>{prompt, role?, analysisType?, repository?,
/// enableTools?, model?, maxBudgetUsd?, cwd?}</c>. The original
/// <c>{TaskType, Context}</c> bound nothing useful.
/// </summary>
public record ExecuteTaskRequest(
    string Prompt,
    string? Role,
    string? AnalysisType,
    string? Repository,
    bool? EnableTools,
    string? Model,
    double? MaxBudgetUsd,
    string? Cwd);

/// <summary>
/// Audit finding 003 — restored TS structured shape. The original
/// <c>{IssueNumber, object Result}</c> dropped the typed <c>exitReason</c>
/// (the primary classification key) plus <c>error</c>, <c>durationMs</c>,
/// <c>repository</c>, and <c>metadata</c>.
/// </summary>
public record CycleResultRequest(
    string ExitReason,
    int? IssueNumber,
    string? Repository,
    string? Error,
    long? DurationMs,
    JsonElement? Metadata);

/// <summary>
/// Engine DCB-event-append shape. One <see cref="EngineEventRecord"/> per
/// <c>TammaEvent</c> the engine drained from its in-process
/// <c>tamma:events</c> transient list. Persisted to the caller's tenant
/// <c>domain_events</c> via <see cref="Tamma.Data.Repositories.IEventRepository"/>.
///
/// <para>The engine emits these events into a write-only transient list that
/// nothing previously drained, and the event repositories are not registered
/// inside <c>Tamma.ElsaServer</c> (it can't reference <c>Tamma.Api</c>). This
/// callback is the durable engine→store bridge — mirrors
/// <see cref="CycleResultRequest"/> / <c>PostCycleResult</c>, the one existing
/// engine→<c>domain_events</c> path.</para>
/// </summary>
public record AppendEventsRequest(
    List<EngineEventRecord> Events);

/// <summary>
/// Wire projection of one engine <c>TammaEvent</c>. <c>eventType</c> becomes
/// <see cref="Tamma.Data.Entities.DomainEvent.Type"/>; the activity/workflow
/// identifiers + <c>status</c> + <c>duration</c> become tenant-scoped
/// <c>Tags</c>; <c>data</c> is the structured payload; <c>timestamp</c> becomes
/// <c>CreatedAt</c>.
/// </summary>
public record EngineEventRecord(
    Guid Id,
    string EventType,
    string? Status,
    string? Error,
    DateTime? Timestamp,
    double? DurationMs,
    string? ActivityId,
    string? ActivityName,
    string? WorkflowInstanceId,
    int? IssueNumber,
    JsonElement? Data,
    Dictionary<string, string?>? Tags);
