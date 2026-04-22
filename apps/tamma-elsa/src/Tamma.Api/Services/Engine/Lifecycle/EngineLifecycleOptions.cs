namespace Tamma.Api.Services.Engine.Lifecycle;

/// <summary>
/// Runtime knobs for the engine lifecycle SSE stream.
/// </summary>
public sealed class EngineLifecycleOptions
{
    /// <summary>
    /// How often the SSE endpoint writes a keep-alive comment frame to each
    /// subscriber while idle. Must be short enough that reverse proxies
    /// (nginx/cloudflare) don't time the connection out; 15s matches the
    /// TS defaults.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);
}
