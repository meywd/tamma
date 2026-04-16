namespace Tamma.Api.Dtos.Engine;

public record SendCommandRequest(string Command, object? Args);
public record StoreContextRequest(int IssueNumber, object Context);
public record QueryContextRequest(string Query);
public record IssueCommentRequest(string Repo, int IssueNumber, string Body);
public record IssueLabelRequest(string Repo, int IssueNumber, string[] Labels);
public record CreateIssueRequest(string Repo, string Title, string? Body, string[]? Labels);
public record TriggerCiRequest(string Repo, string Ref, string Workflow);
public record ExecuteTaskRequest(string TaskType, object? Context);
public record CycleResultRequest(int IssueNumber, object Result);
public record AgentAvailableRequest(string EngineId, string[] Capabilities);
