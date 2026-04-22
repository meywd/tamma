using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Activities.AgentDispatch;
using Tamma.Activities.AgentDispatch.Models;

namespace Tamma.Activities.Tests.AgentDispatch;

[TestFixture]
public class AgentExecutorFactoryTests
{
    private const string EnvVar = "TAMMA_AGENT_MODE";

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(EnvVar, null);
    }

    private static (ServiceProvider sp, IConfiguration cfg) BuildServices(Dictionary<string, string?>? config = null)
    {
        var cfgBuilder = new ConfigurationBuilder();
        if (config is not null) cfgBuilder.AddInMemoryCollection(config);
        var cfg = cfgBuilder.Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<IGitHubActionsClient, NullGitHubActionsClient>();
        services.AddScoped<IAgentDispatchService, AgentDispatchService>();
        services.AddScoped<IAgentMonitorService, AgentMonitorService>();
        services.AddScoped<IAgentResultCollectorService, AgentResultCollectorService>();
        services.AddSingleton<IProcessRunner, DefaultProcessRunner>();
        services.AddSingleton(_ => new LocalExecutorOptions());
        services.AddScoped<LocalExecutor>();
        services.AddScoped<GitHubActionsExecutor>();
        services.AddScoped<AgentExecutorFactory>();

        return (services.BuildServiceProvider(), cfg);
    }

    [Test]
    public void Create_DefaultsToLocal_WhenNoGitHubApp()
    {
        var (sp, _) = BuildServices();
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        var exec = factory.Create();
        exec.Mode.Should().Be(ExecutionModeNames.Local);
    }

    [Test]
    public void Create_UsesGitHubActions_WhenAppConfigured()
    {
        var (sp, _) = BuildServices(new Dictionary<string, string?>
        {
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKey"] = "-----BEGIN KEY-----"
        });
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        var exec = factory.Create();
        exec.Mode.Should().Be(ExecutionModeNames.GitHubActions);
    }

    [Test]
    public void Create_RespectsExplicitConfig()
    {
        var (sp, _) = BuildServices(new Dictionary<string, string?>
        {
            ["Agent:ExecutorMode"] = "github_actions"
        });
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        factory.Create().Mode.Should().Be(ExecutionModeNames.GitHubActions);
    }

    [Test]
    public void Create_EnvVarBeatsConfig()
    {
        Environment.SetEnvironmentVariable(EnvVar, "local");
        var (sp, _) = BuildServices(new Dictionary<string, string?>
        {
            ["Agent:ExecutorMode"] = "github_actions",
            ["GitHub:AppId"] = "12345",
            ["GitHub:PrivateKey"] = "key"
        });
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        factory.Create().Mode.Should().Be(ExecutionModeNames.Local);
    }

    [Test]
    public void Create_ModeOverrideBeatsEverything()
    {
        Environment.SetEnvironmentVariable(EnvVar, "local");
        var (sp, _) = BuildServices();
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        factory.Create("github-actions").Mode.Should().Be(ExecutionModeNames.GitHubActions);
    }

    [Test]
    public void Create_NormalizesAliases()
    {
        var (sp, _) = BuildServices();
        using var scope = sp.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<AgentExecutorFactory>();

        factory.Create("gha").Mode.Should().Be(ExecutionModeNames.GitHubActions);
        factory.Create("github").Mode.Should().Be(ExecutionModeNames.GitHubActions);
        factory.Create("LOCAL").Mode.Should().Be(ExecutionModeNames.Local);
    }
}
