namespace Tamma.Data.Migrations;

/// <summary>
/// Story 37-2 (code-review fix, AC7 hardening) — the append-only defence-in-depth
/// trigger on <c>audit_chain_checkpoints</c> (control-plane resident only). Signed
/// checkpoints are the external anchor that makes tail-truncation detectable, so
/// they must themselves be write-once: this trigger rejects every <c>DELETE</c>
/// and every <c>UPDATE</c>, allowing only <c>INSERT</c>.
///
/// <para><b>Why this matters.</b> <c>record_hash</c> is unkeyed, so the ONLY thing
/// that reveals deletion of recent records is a surviving signed checkpoint whose
/// <c>head_sequence</c> exceeds the current chain head. If checkpoints were
/// deletable, an attacker could delete the recent records AND the covering
/// checkpoint and the chain would verify as <c>Ok</c>. Mirroring the
/// <see cref="AuditRecordsAppendOnlyTrigger"/> on the checkpoint table closes
/// that hole for accidental/ORM writes; the cryptographic HMAC signature closes
/// it against forgery.</para>
///
/// <para><b>Not a security boundary.</b> A superuser / <c>ALTER TABLE … DISABLE
/// TRIGGER</c> bypasses it — which is exactly why the checkpoints are signed with
/// an out-of-cabinet key. The trigger stops accidental writes; the verification
/// (head-sequence &gt;= max-checkpoint-head) + signature make deliberate tampering
/// DETECTABLE. Document this in the runbook.</para>
/// </summary>
internal static class AuditChainCheckpointsAppendOnlyTrigger
{
    public const string UpSql = """
        CREATE OR REPLACE FUNCTION audit_chain_checkpoints_append_only()
        RETURNS trigger AS $$
        BEGIN
          IF (TG_OP = 'DELETE') THEN
            RAISE EXCEPTION 'audit_chain_checkpoints is append-only: DELETE rejected (row %).', OLD."Id";
          END IF;
          -- Checkpoints are write-once; any UPDATE is forbidden.
          RAISE EXCEPTION 'audit_chain_checkpoints is append-only: UPDATE rejected (row %).', OLD."Id";
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_audit_chain_checkpoints_append_only ON audit_chain_checkpoints;
        CREATE TRIGGER trg_audit_chain_checkpoints_append_only
          BEFORE UPDATE OR DELETE ON audit_chain_checkpoints
          FOR EACH ROW EXECUTE FUNCTION audit_chain_checkpoints_append_only();
        """;

    public const string DownSql = """
        DROP TRIGGER IF EXISTS trg_audit_chain_checkpoints_append_only ON audit_chain_checkpoints;
        DROP FUNCTION IF EXISTS audit_chain_checkpoints_append_only();
        """;
}
