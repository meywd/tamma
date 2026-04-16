namespace Tamma.Api.Dtos.Prompts;

public record UpsertPromptRequest(string Template, string? SystemPrompt, string[]? Variables, bool? EnableTools, int? MaxTokens);
public record RenderPromptRequest(Dictionary<string, string> Variables);
public record PromptResponse(string? Role, string? Action, string Template, string? SystemPrompt, string[]? Variables, bool EnableTools, int MaxTokens, string Source);
public record RenderedPromptResponse(string SystemPrompt, string UserPrompt);
