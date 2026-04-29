using System.Collections.Concurrent;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// In-memory cache of short-lived Gitea OAuth2 access tokens, keyed by
/// installation id. Threadsafe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///
/// <para>Refresh policy (impl-plan §2):</para>
/// <list type="bullet">
///   <item>TTL = <c>expires_in - 60s</c> safety margin so a token never
///         expires mid-call.</item>
///   <item>On 401 from Gitea, <see cref="GiteaHttpClient"/> calls
///         <see cref="Invalidate"/> + retries via the refresh-token
///         exchange exactly once.</item>
/// </list>
///
/// <para>Bot tokens never enter this cache — only OAuth2 mode uses
/// it.</para>
/// </summary>
public sealed class GiteaOAuth2TokenCache
{
    private readonly ConcurrentDictionary<Guid, CachedToken> _entries = new();

    /// <summary>
    /// Try to read a cached non-expired access token for the given
    /// installation. Returns null when no entry exists or the entry has
    /// expired (we do NOT auto-evict; <see cref="Set"/> overwrites).
    /// </summary>
    public string? TryGet(Guid installationId)
    {
        if (_entries.TryGetValue(installationId, out var entry)
            && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return entry.AccessToken;
        }
        return null;
    }

    /// <summary>
    /// Cache a freshly minted access token. <paramref name="ttl"/>
    /// SHOULD already include the 60s safety margin.
    /// </summary>
    public void Set(Guid installationId, string accessToken, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        if (ttl <= TimeSpan.Zero)
        {
            // Caller passed a non-positive TTL — refuse to cache;
            // the next call will refresh.
            return;
        }
        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        _entries[installationId] = new CachedToken(accessToken, expiresAt);
    }

    /// <summary>
    /// Drop the cached token for an installation. Called on 401.
    /// </summary>
    public void Invalidate(Guid installationId)
    {
        _entries.TryRemove(installationId, out _);
    }

    /// <summary>
    /// Test hook — current entry count.
    /// </summary>
    internal int Count => _entries.Count;

    private readonly record struct CachedToken(
        string AccessToken,
        DateTimeOffset ExpiresAt);
}
