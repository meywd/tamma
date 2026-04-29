using DotNet.Testcontainers.Containers;

namespace Tamma.Platforms.IntegrationTests.Fixtures;

/// <summary>
/// Story 31-10 — base class for every platform integration test
/// fixture. Concrete subclasses (e.g.
/// <see cref="GiteaContainerFixture"/>) own the platform-specific
/// container image, healthcheck, admin/bot/repo seeding, and PAT
/// minting. The base coordinates the lifecycle and ferries the
/// runtime artifacts (BaseUrl, BotToken, OwnerLogin, RepoName,
/// WebhookSecret) that the contract test class consumes.
///
/// <para>Container choice rationale: real platform server, real HTTP,
/// no mocks. The fixture's whole point is to catch the kind of bugs
/// WireMock fixtures hide — auth-token lifecycle quirks, error-shape
/// drift, pagination quirks, file-content base64 encoding, webhook
/// header naming, etc.</para>
/// </summary>
public abstract class PlatformIntegrationFixture : IAsyncDisposable
{
    /// <summary>
    /// HTTP base URL of the running platform (e.g.
    /// <c>http://localhost:32768</c>). Set in <see cref="StartAsync"/>.
    /// </summary>
    public string BaseUrl { get; protected set; } = string.Empty;

    /// <summary>
    /// PAT for the bot user the fixture creates during boot. The
    /// contract test wires this into the driver's
    /// <c>credentialPlaintext</c> argument.
    /// </summary>
    public string BotToken { get; protected set; } = string.Empty;

    /// <summary>
    /// Login of the bot user — also the owner of the fixture repo.
    /// Used by tests as <c>owner</c> in driver method calls.
    /// </summary>
    public string OwnerLogin { get; protected set; } = string.Empty;

    /// <summary>
    /// Fixture repo name owned by <see cref="OwnerLogin"/>. Tests pass
    /// this as <c>repoName</c>.
    /// </summary>
    public string RepoName { get; protected set; } = "test-repo";

    /// <summary>
    /// Secret the fixture seeds into webhook registrations + uses to
    /// verify HMAC round-trip in
    /// <c>RegisterWebhook_RoundTrip</c>.
    /// </summary>
    public string WebhookSecret { get; protected set; } =
        Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    /// <summary>
    /// Default branch the fixture creates the repo with. Drivers
    /// typically default to <c>main</c>.
    /// </summary>
    public string DefaultBranch { get; protected set; } = "main";

    /// <summary>
    /// Tip SHA of <see cref="DefaultBranch"/> at fixture-ready time.
    /// Tests use this as the source ref for <c>CreateBranchAsync</c>.
    /// </summary>
    public string DefaultBranchSha { get; protected set; } = string.Empty;

    /// <summary>
    /// True when <see cref="StartAsync"/> succeeded. Tests that depend
    /// on a started fixture <c>Assert.Inconclusive</c> when this is
    /// false rather than blowing up the suite.
    /// </summary>
    public bool IsReady { get; protected set; }

    /// <summary>
    /// Boot the container, wait for healthy, run the seed script.
    /// Idempotent — calling it twice is a programming error.
    /// </summary>
    public abstract Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Tear down the container + dispose any held resources.
    /// </summary>
    public abstract ValueTask DisposeAsync();

    /// <summary>
    /// Helper: poll a container's healthcheck endpoint until 200 OK or
    /// the timeout expires. Throws on timeout — concrete subclasses
    /// rethrow as a fixture-specific failure with a useful log payload.
    /// </summary>
    protected static async Task PollHealthAsync(
        string url,
        TimeSpan timeout,
        CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 200 and < 300)
                {
                    return;
                }
                last = new Exception($"healthcheck got HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"healthcheck for {url} did not return 200 within {timeout.TotalSeconds}s",
            last);
    }

    /// <summary>
    /// Helper: spit a snapshot of the container's recent stdout/stderr
    /// for failure diagnostics. Concrete fixtures call this when
    /// throwing in <see cref="StartAsync"/>.
    /// </summary>
    protected static async Task<string> CaptureContainerLogsAsync(
        IContainer container, CancellationToken ct)
    {
        try
        {
            var (stdout, stderr) = await container.GetLogsAsync(ct: ct).ConfigureAwait(false);
            // 4 KB tail is enough for healthcheck-failure triage
            // without flooding the test output.
            return Tail(stdout, 2048) + "\n--- stderr ---\n" + Tail(stderr, 2048);
        }
        catch
        {
            return "(failed to capture container logs)";
        }
    }

    private static string Tail(string s, int max) =>
        string.IsNullOrEmpty(s) ? string.Empty
            : s.Length <= max ? s
            : s[^max..];
}
