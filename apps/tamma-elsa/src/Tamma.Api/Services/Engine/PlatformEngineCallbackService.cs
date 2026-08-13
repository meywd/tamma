using System.Text;
using System.Text.Json;
using Tamma.Core.Logging;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;
using Tamma.Platforms.Abstractions;
using PModels = Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Engine;

/// <summary>
/// Epic 31 P3 (seam 5) — the PLATFORM-AGNOSTIC engine-callback surface behind
/// the <c>/api/engine/*</c> git-proxy handlers (repo-config, issues,
/// security-alerts, issue-comment, labels±, create-issue). Replaces the
/// GitHub-only <c>IGitHubEngineCallbackService</c>: every operation resolves
/// the acting tenant's platform driver via
/// <see cref="IPlatformResolver.ResolveForMediationAsync"/> (tenant
/// installation → <c>Platform:</c> config tier) and speaks only
/// <see cref="IGitPlatformClient"/> — this is what lets the loop SELECT WORK
/// and READ CONVENTIONS off-GitHub.
///
/// <para>The result envelope + the handlers' response shapes are UNCHANGED
/// (pinned by <c>EngineCallbackContractTests</c>): no resolvable driver ⇒ the
/// legacy 503 <c>github_client_not_configured</c> envelope
/// (<see cref="GitHubCallbackResult{T}.NotConfigured"/> — now meaning "no
/// platform driver resolved"); platform failures ⇒ <c>Failed(reason)</c>
/// (the 502 arm) with <see cref="PlatformErrorText.ToLegacyString"/> wire
/// strings.</para>
///
/// <para><b>Capability degradation (plan §4).</b> A typed
/// <c>capability_unsupported</c> on the security-alert read degrades to EMPTY
/// alert lists with one <c>ENGINE.SECURITY_ALERTS.SKIPPED</c> DCB audit event
/// (never silent, never a hard failure — a platform without a security-alert
/// surface must not stall triage). All other verbs surface the typed code to
/// the caller.</para>
/// </summary>
public interface IEngineGitCallbackService
{
    Task<GitHubCallbackResult<JsonElement>> ReadRepoConfigAsync(
        Guid? tenantId, string owner, string repo, string branch, CancellationToken ct = default);

    Task<GitHubCallbackResult<IssueListResult>> ListIssuesAsync(
        Guid? tenantId, string owner, string repo, string state, string? labels, int perPage, int page,
        CancellationToken ct = default);

    Task<GitHubCallbackResult<SecurityAlertResult>> ListSecurityAlertsAsync(
        Guid? tenantId, string owner, string repo, string alertType, CancellationToken ct = default);

    Task<GitHubCallbackResult<IssueCommentResult>> PostIssueCommentAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string body, CancellationToken ct = default);

    Task<GitHubCallbackResult<string[]>> AddIssueLabelsAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default);

    Task<GitHubCallbackResult<bool>> RemoveIssueLabelAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string label, CancellationToken ct = default);

    Task<GitHubCallbackResult<CreatedIssueResult>> CreateIssueAsync(
        Guid? tenantId, string owner, string repo, string title, string? body,
        string[]? labels, string[]? assignees, CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class PlatformEngineCallbackService : IEngineGitCallbackService
{
    /// <summary>DCB audit event for a capability-degraded security-alert read.</summary>
    internal const string SecurityAlertsSkippedEventType = "ENGINE.SECURITY_ALERTS.SKIPPED";

    private static readonly string[] RepoConfigPaths =
        [".tamma/config.yaml", ".tamma/config.yml", ".tamma/config.json"];

    private readonly IPlatformResolver _resolver;
    private readonly IEventRepository _events;
    private readonly ILogger<PlatformEngineCallbackService> _logger;
    private readonly Tamma.Data.Repositories.IInstallationRepository? _appInstallations;

    public PlatformEngineCallbackService(
        IPlatformResolver resolver,
        IEventRepository events,
        ILogger<PlatformEngineCallbackService> logger,
        Tamma.Data.Repositories.IInstallationRepository? appInstallations = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _appInstallations = appInstallations;
    }

    /// <summary>
    /// Epic 31 review (F-high) — PER-REPO installation resolution first,
    /// tenant-primary mediation resolution second. The pre-Epic-31 engine
    /// callback resolved the App installation PER REPO; the P3 swap replaced
    /// that with tenant-primary, so a tenant with the App on multiple
    /// installations got 404s (a GitHub App installation token cannot see a
    /// sibling installation's repos) on every repo of the non-primary
    /// installation — work selection silently stopped there.
    /// </summary>
    private async Task<IGitPlatformClient?> ResolveClientAsync(
        Guid? tenantId, string owner, string repo, CancellationToken ct)
    {
        if (tenantId is { } tid && tid != Guid.Empty && _appInstallations is not null)
        {
            try
            {
                var install = await _appInstallations
                    .GetByRepoFullNameAsync($"{owner}/{repo}")
                    .ConfigureAwait(false);
                if (install?.TenantId == tid)
                {
                    var perRepo = await _resolver.ResolveForRepoInstallationAsync(
                        tid, PlatformKind.GitHub,
                        install.InstallationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ct).ConfigureAwait(false);
                    if (perRepo is not null) return perRepo.Client;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Per-repo installation resolution failed for {Owner}/{Repo}; "
                    + "falling back to tenant-primary resolution", owner, repo);
            }
        }

        var resolution = await _resolver.ResolveForMediationAsync(tenantId, ct).ConfigureAwait(false);
        return resolution?.Driver.Client;
    }

    // ─── Repo config ────────────────────────────────────────────────────────

    public async Task<GitHubCallbackResult<JsonElement>> ReadRepoConfigAsync(
        Guid? tenantId, string owner, string repo, string branch, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<JsonElement>.NotConfigured();

        foreach (var path in RepoConfigPaths)
        {
            var res = await client.GetFileContentAsync(
                new PModels.GetFileContentRequest(owner, repo, path, branch), ct).ConfigureAwait(false);
            if (res is not PlatformResult<byte[]>.Ok ok)
            {
                continue; // not found / unreadable — try the next path (TS contract).
            }

            var content = Encoding.UTF8.GetString(ok.Value);
            if (string.IsNullOrWhiteSpace(content)) continue;

            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(content);
                    return GitHubCallbackResult<JsonElement>.Ok(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    break; // invalid JSON in repo → graceful {} (TS contract).
                }
            }

            var envelope = JsonSerializer.SerializeToDocument(new { rawYaml = content });
            return GitHubCallbackResult<JsonElement>.Ok(envelope.RootElement.Clone());
        }

        // No config file — {} keeps the conventions-injection path working.
        using var empty = JsonDocument.Parse("{}");
        return GitHubCallbackResult<JsonElement>.Ok(empty.RootElement.Clone());
    }

    // ─── Issues list (the loop's WORK SELECTION read) ───────────────────────

    public async Task<GitHubCallbackResult<IssueListResult>> ListIssuesAsync(
        Guid? tenantId, string owner, string repo, string state, string? labels, int perPage, int page,
        CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<IssueListResult>.NotConfigured();

        var labelList = string.IsNullOrWhiteSpace(labels)
            ? null
            : labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var res = await client.ListIssuesAsync(
            new PModels.ListIssuesRequest(owner, repo, state, labelList, perPage, page), ct).ConfigureAwait(false);
        if (res is not PlatformResult<IReadOnlyList<PModels.Issue>>.Ok ok)
            return Fail<IssueListResult>(res);

        var projected = ok.Value.Select(IssueToJson).ToList();
        return GitHubCallbackResult<IssueListResult>.Ok(new IssueListResult(projected, projected.Count));
    }

    // ─── Security alerts (capability-degrading read) ────────────────────────

    public async Task<GitHubCallbackResult<SecurityAlertResult>> ListSecurityAlertsAsync(
        Guid? tenantId, string owner, string repo, string alertType, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<SecurityAlertResult>.NotConfigured();

        var res = await client.ListSecurityAlertsAsync(owner, repo, alertType, ct).ConfigureAwait(false);
        if (res is PlatformResult<PModels.SecurityAlerts>.Ok ok)
        {
            return GitHubCallbackResult<SecurityAlertResult>.Ok(new SecurityAlertResult(
                ok.Value.DependabotJson.Select(ParseElement).ToList(),
                ok.Value.CodeScanningJson.Select(ParseElement).ToList()));
        }

        // §4 — a platform WITHOUT a security-alert surface degrades to empty
        // lists with a LOUD audit event (skip-with-audit, never silent, never
        // a hard failure that stalls triage).
        if (res is PlatformResult<PModels.SecurityAlerts>.Failed f
            && PlatformErrorText.IsCapabilityUnsupported(f.Error))
        {
            await EmitSkippedAsync(tenantId, $"{owner}/{repo}", alertType, ct).ConfigureAwait(false);
            return GitHubCallbackResult<SecurityAlertResult>.Ok(
                new SecurityAlertResult([], []));
        }

        return Fail<SecurityAlertResult>(res);
    }

    // ─── Issue comment / labels / create ────────────────────────────────────

    public async Task<GitHubCallbackResult<IssueCommentResult>> PostIssueCommentAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string body, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<IssueCommentResult>.NotConfigured();

        var res = await client.CreateIssueCommentAsync(
            owner, repo, issueNumber.ToString(), body, ct).ConfigureAwait(false);
        if (res is not PlatformResult<PModels.IssueComment>.Ok ok)
            return Fail<IssueCommentResult>(res);

        // The platform-neutral IssueComment carries no HtmlUrl; the wire shape
        // keeps the field (empty) so deployed activities parse unchanged.
        var id = long.TryParse(ok.Value.Id, out var parsed) ? parsed : 0;
        return GitHubCallbackResult<IssueCommentResult>.Ok(new IssueCommentResult(id, string.Empty));
    }

    public async Task<GitHubCallbackResult<string[]>> AddIssueLabelsAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string[] labels, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<string[]>.NotConfigured();

        var res = await client.AddIssueLabelsAsync(
            new PModels.AddIssueLabelsRequest(owner, repo, issueNumber.ToString(), labels), ct).ConfigureAwait(false);
        if (res is not PlatformResult<IReadOnlyList<string>>.Ok ok)
            return Fail<string[]>(res);
        return GitHubCallbackResult<string[]>.Ok(ok.Value.ToArray());
    }

    public async Task<GitHubCallbackResult<bool>> RemoveIssueLabelAsync(
        Guid? tenantId, string owner, string repo, int issueNumber, string label, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<bool>.NotConfigured();

        var res = await client.RemoveIssueLabelAsync(
            owner, repo, issueNumber.ToString(), label, ct).ConfigureAwait(false);
        if (res is not PlatformResult<IReadOnlyList<string>>.Ok)
            return Fail<bool>(res);
        return GitHubCallbackResult<bool>.Ok(true);
    }

    public async Task<GitHubCallbackResult<CreatedIssueResult>> CreateIssueAsync(
        Guid? tenantId, string owner, string repo, string title, string? body,
        string[]? labels, string[]? assignees, CancellationToken ct = default)
    {
        var client = await ResolveClientAsync(tenantId, owner, repo, ct).ConfigureAwait(false);
        if (client is null) return GitHubCallbackResult<CreatedIssueResult>.NotConfigured();

        var res = await client.CreateIssueAsync(
            new PModels.CreateIssueRequest(owner, repo, title, body, labels, assignees), ct).ConfigureAwait(false);
        if (res is not PlatformResult<PModels.Issue>.Ok ok)
            return Fail<CreatedIssueResult>(res);

        var number = int.TryParse(ok.Value.Number, out var n) ? n : 0;
        // The REAL platform URL — never a fabricated https://github.com/… one.
        return GitHubCallbackResult<CreatedIssueResult>.Ok(
            new CreatedIssueResult(number, ok.Value.HtmlUrl, ok.Value.Title));
    }

    // ─── Shared plumbing ────────────────────────────────────────────────────

    /// <summary>Project a non-Ok platform result into the envelope's Failed arm
    /// using the same status-prefixed wire-string family the mediation planes
    /// surface (the 502 <c>{error}</c> handler arm's reason string).</summary>
    private static GitHubCallbackResult<T> Fail<T>(object result)
    {
        var reason = result switch
        {
            PlatformResult<IReadOnlyList<PModels.Issue>>.Failed f => Describe(f.Error),
            PlatformResult<PModels.Issue>.Failed f => Describe(f.Error),
            PlatformResult<PModels.SecurityAlerts>.Failed f => Describe(f.Error),
            PlatformResult<PModels.IssueComment>.Failed f => Describe(f.Error),
            PlatformResult<IReadOnlyList<string>>.Failed f => Describe(f.Error),
            _ => "503: platform unavailable",
        };
        return GitHubCallbackResult<T>.Failed(reason);
    }

    private static string Describe(PlatformError error) =>
        PlatformErrorText.IsCapabilityUnsupported(error)
            ? PlatformErrorText.CapabilityUnsupportedCode
            : PlatformErrorText.ToLegacyString(error);

    private async Task EmitSkippedAsync(Guid? tenantId, string repo, string alertType, CancellationToken ct)
    {
        _ = ct;
        try
        {
            await _events.AppendAsync(new DomainEvent
            {
                Id = Guid.NewGuid(),
                Type = SecurityAlertsSkippedEventType,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(new
                {
                    tenantId = tenantId?.ToString(),
                    repo,
                    failureCode = PlatformErrorText.CapabilityUnsupportedCode,
                }),
                Metadata = JsonSerializer.Serialize(new { workflowVersion = "1.0.0", eventSource = "system" }),
                Data = JsonSerializer.Serialize(new
                {
                    alertType,
                    detail = "the resolved platform has no security-alert surface; triage proceeds with empty alert lists",
                }),
                CreatedAt = DateTime.UtcNow,
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ENGINE.SECURITY_ALERTS.SKIPPED event append failed for {Repo}; the degraded read still returns",
                LogSanitizer.Clean(repo));
        }
    }

    /// <summary>The minimal issue projection the deployed activities parse
    /// (unchanged shape; <c>id</c> carries the platform-scoped issue number —
    /// platform-neutral drivers do not surface GitHub's internal row id).</summary>
    internal static JsonElement IssueToJson(PModels.Issue issue)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", long.TryParse(issue.Number, out var n) ? n : 0);
            writer.WriteNumber("number", long.TryParse(issue.Number, out var n2) ? n2 : 0);
            writer.WriteString("title", issue.Title);
            writer.WriteString("state", issue.State == PModels.IssueState.Closed ? "closed" : "open");
            writer.WriteString("body", issue.Body ?? string.Empty);
            writer.WriteString("html_url", issue.HtmlUrl);
            writer.WriteStartArray("labels");
            foreach (var label in issue.Labels)
            {
                writer.WriteStartObject();
                writer.WriteString("name", label);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }

    private static JsonElement ParseElement(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }
}
