namespace Tamma.Data.Entities;

/// <summary>
/// Story 28-R2 / PF-S9 — single-row sentinel that records which user
/// owned the bootstrap superadmin promotion. The table has a
/// <c>CHECK (Id = 1)</c> constraint and a unique primary key, so the
/// schema mathematically permits at most one row. Concurrent
/// first-user registrations both attempt the insert; exactly one wins
/// (UNIQUE-violation), the other catches the conflict and falls back
/// to a normal <c>"user"</c> platform role.
///
/// <para><b>Why a table, not a count + insert</b>: <c>SELECT COUNT(*)
/// FROM users WHERE platform_role = 'platform_admin'</c> followed by
/// an insert into <c>users</c> is a TOCTOU race — two concurrent
/// transactions both see the count as 0 and both insert with
/// <c>platform_admin</c>. Wrapping in <c>SERIALIZABLE</c> would work
/// but forces every registration through the strictest isolation
/// level. A dedicated single-row table is the explicit, schema-level
/// guard: the DB itself rejects the second concurrent claim.</para>
///
/// <para>The row is created exactly once in the lifetime of a
/// deployment. <c>UserId</c> is the user that ended up as the
/// platform_admin; <c>ClaimedAt</c> is the timestamp for forensics
/// (which registration won the race). The row is never updated or
/// deleted — once a deployment has its bootstrap admin, that's the
/// sentinel forever.</para>
/// </summary>
public class PlatformBootstrap
{
    /// <summary>
    /// Hard-coded sentinel id. The DB-level CHECK constraint
    /// (<c>Id = 1</c>) prevents a second row from ever being
    /// inserted, regardless of application-side bugs.
    /// </summary>
    public const int SentinelId = 1;

    public int Id { get; set; } = SentinelId;

    /// <summary>
    /// User that won the bootstrap-superadmin race. FK is RESTRICT so
    /// you can't accidentally delete the original platform admin.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Timestamp the bootstrap row was inserted. Default = <c>now()</c>.
    /// </summary>
    public DateTime ClaimedAt { get; set; }
}
