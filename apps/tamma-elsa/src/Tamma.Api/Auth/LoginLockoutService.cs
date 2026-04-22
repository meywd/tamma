using System.Collections.Concurrent;

namespace Tamma.Api.Auth;

public interface ILoginLockoutService
{
    bool RecordFailedAttempt(string email);
    bool IsLocked(string email);
    void ResetAttempts(string email);
    int GetRemainingLockoutSeconds(string email);
}

public class LoginLockoutService : ILoginLockoutService
{
    private const int MaxAttempts = 5;
    private const int WindowMinutes = 15;
    private const int LockoutMinutes = 30;

    private readonly ConcurrentDictionary<string, LockoutEntry> _entries = new();

    public bool RecordFailedAttempt(string email)
    {
        var key = email.ToLowerInvariant();
        var entry = _entries.GetOrAdd(key, _ => new LockoutEntry());

        lock (entry)
        {
            var now = DateTime.UtcNow;

            // If a previous lockout has expired, clear it AND the residual
            // timestamps so the new attempt starts a fresh window.
            // Mirrors TS login-lockout.ts:78-82 — audit finding 017.
            if (entry.LockedUntil.HasValue && entry.LockedUntil.Value <= now)
            {
                entry.LockedUntil = null;
                entry.Attempts.Clear();
            }

            // Still locked? Don't add more attempts; just signal locked.
            if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > now)
                return true;

            // Clean old attempts outside the rolling window.
            var cutoff = now.AddMinutes(-WindowMinutes);
            entry.Attempts.RemoveAll(a => a < cutoff);

            entry.Attempts.Add(now);

            if (entry.Attempts.Count >= MaxAttempts)
            {
                entry.LockedUntil = now.AddMinutes(LockoutMinutes);
                return true;
            }
        }

        return false;
    }

    public bool IsLocked(string email)
    {
        var key = email.ToLowerInvariant();
        if (!_entries.TryGetValue(key, out var entry))
            return false;

        lock (entry)
        {
            if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > DateTime.UtcNow)
                return true;

            if (entry.LockedUntil.HasValue && entry.LockedUntil.Value <= DateTime.UtcNow)
            {
                entry.LockedUntil = null;
                entry.Attempts.Clear();
            }
        }

        return false;
    }

    public void ResetAttempts(string email)
    {
        var key = email.ToLowerInvariant();
        _entries.TryRemove(key, out _);
    }

    public int GetRemainingLockoutSeconds(string email)
    {
        var key = email.ToLowerInvariant();
        if (!_entries.TryGetValue(key, out var entry))
            return 0;

        lock (entry)
        {
            if (entry.LockedUntil.HasValue && entry.LockedUntil.Value > DateTime.UtcNow)
                return (int)(entry.LockedUntil.Value - DateTime.UtcNow).TotalSeconds;
        }

        return 0;
    }

    private class LockoutEntry
    {
        public List<DateTime> Attempts { get; } = [];
        public DateTime? LockedUntil { get; set; }
    }
}
