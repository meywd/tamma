using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Tamma.Activities.AgentDispatch;

/// <summary>
/// Resolves which <see cref="IAgentExecutor"/> to use for a given
/// workflow invocation (story 19-5 AC-4).
///
/// <para>Precedence, highest to lowest:</para>
/// <list type="number">
///   <item><c>modeOverride</c> argument (set by <c>ExecuteAgentActivity</c>
///     from the workflow input).</item>
///   <item>Environment variable <c>TAMMA_AGENT_MODE</c>.</item>
///   <item>Configuration key <c>Agent:ExecutorMode</c>.</item>
///   <item>Auto-detection: GitHubActions if a GitHub App is configured
///     (<c>GitHub:AppId</c> + <c>GitHub:PrivateKey</c>), else Local.</item>
/// </list>
/// </summary>
public sealed class AgentExecutorFactory
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AgentExecutorFactory>? _logger;

    public AgentExecutorFactory(
        IServiceProvider services,
        IConfiguration configuration,
        ILogger<AgentExecutorFactory>? logger = null)
    {
        _services = services;
        _configuration = configuration;
        _logger = logger;
    }

    public IAgentExecutor Create(string? modeOverride = null)
    {
        var mode = Resolve(modeOverride);
        _logger?.LogDebug("AgentExecutorFactory resolved mode={Mode}", mode);

        return mode switch
        {
            Models.ExecutionModeNames.Local => _services.GetRequiredService<LocalExecutor>(),
            Models.ExecutionModeNames.GitHubActions => _services.GetRequiredService<GitHubActionsExecutor>(),
            _ => throw new ArgumentException($"Unknown agent execution mode: {mode}")
        };
    }

    private string Resolve(string? modeOverride)
    {
        if (!string.IsNullOrEmpty(modeOverride))
        {
            return Normalize(modeOverride);
        }

        var envVar = Environment.GetEnvironmentVariable("TAMMA_AGENT_MODE");
        if (!string.IsNullOrEmpty(envVar))
        {
            return Normalize(envVar);
        }

        var configured = _configuration["Agent:ExecutorMode"];
        if (!string.IsNullOrEmpty(configured) && !string.Equals(configured, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(configured);
        }

        // Auto detection.
        var hasGitHubApp =
            _configuration.GetValue<long?>("GitHub:AppId") is long appId && appId > 0
            && !string.IsNullOrWhiteSpace(_configuration["GitHub:PrivateKey"]);

        return hasGitHubApp
            ? Models.ExecutionModeNames.GitHubActions
            : Models.ExecutionModeNames.Local;
    }

    private static string Normalize(string raw)
    {
        return raw.Trim().ToLowerInvariant() switch
        {
            "local" => Models.ExecutionModeNames.Local,
            "github" or "github_actions" or "github-actions" or "gha"
                => Models.ExecutionModeNames.GitHubActions,
            _ => raw.ToLowerInvariant()
        };
    }
}
