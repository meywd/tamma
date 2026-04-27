namespace Tamma.Data.Entities;

/// <summary>
/// R2-H14: durable record of a KEK rotation. The previous design held
/// the staged secondary KEK only in <see cref="Tamma.Api.Services.Secrets.KekProvider"/>'s
/// in-memory <c>_secondary</c> field; a process crash mid-rotation
/// dropped the new key and forced operators to mint a fresh one,
/// orphaning any rows that had already been re-encrypted under the
/// lost key.
///
/// <para>Each rotation now opens with a row in <c>kek_rotations</c>
/// carrying the staged-secondary KEK (encrypted by the OLD primary so
/// the row is readable across restarts). On startup the coordinator
/// scans for non-terminal rows and either resumes the rotation or
/// rolls it back. The advisory-lock contention check
/// (<see cref="Tamma.Api.Services.Secrets.KekRotationCoordinator"/>'s
/// <c>pg_try_advisory_lock</c> call) ensures only one process can
/// mutate the active row at a time.</para>
///
/// <para>Status transitions: <c>pending → running → completed</c> on
/// success; <c>pending → running → failed</c> on per-row decrypt
/// failure; <c>pending → cancelled</c> when an operator pulls the
/// rotation before any work has happened. The
/// <c>StagedSecondaryProtected</c> column is zeroed out when the
/// row leaves <c>pending</c>/<c>running</c>.</para>
/// </summary>
public class KekRotation
{
    public Guid Id { get; set; }
    public string Status { get; set; } = "pending";
    public int VersionOld { get; set; }
    public int VersionNew { get; set; }
    public byte[]? StagedSecondaryProtected { get; set; }
    public string? FailureReason { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}
