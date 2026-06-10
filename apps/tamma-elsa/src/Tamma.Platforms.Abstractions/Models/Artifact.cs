namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// CI artifact descriptor. The actual bytes are downloaded via
/// <see cref="IGitPlatformActionsClient.DownloadArtifactAsync"/>.
/// </summary>
/// <param name="Id">Platform-scoped id.</param>
/// <param name="Name">Artifact name as the producer set it.</param>
/// <param name="SizeBytes">Size in bytes (may be 0 if expired).</param>
/// <param name="DownloadUrl">
/// Platform download URL. May be a redirect to a short-lived signed
/// URL — drivers handle redirects internally so callers can also pass
/// this URL straight into a sandboxed downloader.
/// </param>
public sealed record Artifact(
    string Id,
    string Name,
    long SizeBytes,
    string DownloadUrl);
