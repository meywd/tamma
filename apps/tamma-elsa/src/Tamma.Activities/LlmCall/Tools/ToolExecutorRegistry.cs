using Microsoft.Extensions.Logging;

namespace Tamma.Activities.LlmCall.Tools;

/// <summary>
/// Default registry populated via DI (IEnumerable&lt;IToolExecutor&gt;).
/// Tool names are case-insensitive.
/// </summary>
public class ToolExecutorRegistry : IToolExecutorRegistry
{
    private readonly Dictionary<string, IToolExecutor> _executors;
    private readonly ILogger<ToolExecutorRegistry> _logger;

    public ToolExecutorRegistry(
        IEnumerable<IToolExecutor> executors,
        ILogger<ToolExecutorRegistry> logger)
    {
        _logger = logger;
        _executors = new Dictionary<string, IToolExecutor>(StringComparer.OrdinalIgnoreCase);

        foreach (var executor in executors)
        {
            if (_executors.ContainsKey(executor.ToolName))
            {
                _logger.LogWarning(
                    "Duplicate tool executor registration for '{ToolName}', keeping first",
                    executor.ToolName);
                continue;
            }

            _executors[executor.ToolName] = executor;
            _logger.LogDebug("Registered tool executor: {ToolName}", executor.ToolName);
        }

        _logger.LogInformation(
            "ToolExecutorRegistry initialized with {RegisteredToolCount} tools: {ToolNames}",
            _executors.Count,
            string.Join(", ", _executors.Keys));
    }

    /// <inheritdoc/>
    public IToolExecutor? GetExecutor(string toolName)
    {
        if (!_executors.TryGetValue(toolName, out var executor))
        {
            _logger.LogWarning(
                "Tool executor not found: {ToolName}. Registered count: {RegisteredToolCount}",
                toolName, _executors.Count);
            return null;
        }

        return executor;
    }

    /// <inheritdoc/>
    public bool IsAllowed(string toolName, string[]? allowlist)
    {
        if (allowlist is null || allowlist.Length == 0)
            return true;

        return allowlist.Any(a => string.Equals(a, toolName, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc/>
    public IReadOnlyList<IToolExecutor> GetAll()
        => _executors.Values.ToList().AsReadOnly();

    /// <inheritdoc/>
    public IReadOnlyList<IToolExecutor> GetAllowed(string[]? allowlist)
    {
        if (allowlist is null || allowlist.Length == 0)
            return GetAll();

        return _executors.Values
            .Where(e => allowlist.Any(a =>
                string.Equals(a, e.ToolName, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }
}
