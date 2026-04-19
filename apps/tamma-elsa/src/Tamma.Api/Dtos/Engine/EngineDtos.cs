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
