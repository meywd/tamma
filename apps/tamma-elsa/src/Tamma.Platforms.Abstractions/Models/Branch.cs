namespace Tamma.Platforms.Abstractions.Models;

/// <summary>
/// Platform-neutral branch record.
/// </summary>
/// <param name="Name">Branch short name (e.g. <c>feature/foo</c>).</param>
/// <param name="Sha">Tip commit SHA at the time of the call.</param>
/// <param name="Protected">
/// True when the platform reports branch protection rules apply.
/// Drivers MAY return false for platforms that don't expose this in
/// the list response (Story 31-4 Gitea). Don't trust this for
/// security decisions — re-check via the dedicated protection API.
/// </param>
public sealed record Branch(
    string Name,
    string Sha,
    bool Protected);
