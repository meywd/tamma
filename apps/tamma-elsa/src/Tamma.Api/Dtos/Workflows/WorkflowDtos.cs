namespace Tamma.Api.Dtos.Workflows;

public record CreateDefinitionRequest(string Name, string? Description, object? Steps);
public record CreateInstanceRequest(Guid DefinitionId, object? Variables);
public record UpdateInstanceRequest(string? Status, string? CurrentActivity, object? Variables);
public record DefinitionResponse(Guid Id, string Name, int Version, string? Description, DateTime SyncedAt);
public record InstanceResponse(Guid Id, Guid DefinitionId, string Status, string? CurrentActivity, DateTime CreatedAt, DateTime UpdatedAt);
