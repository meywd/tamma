namespace Tamma.Data.Migrations;

/// <summary>
/// Story 37-2 (AC11) — the append-only defence-in-depth trigger on
/// <c>audit_records</c>, applied to BOTH the control-plane and per-tenant stores
/// (each schema gets its own copy). Rejects <c>DELETE</c> and any <c>UPDATE</c>
/// that mutates a core/immutable field or an ALREADY-SET chain column; it allows
/// a one-time NULL→value backfill of the chain columns (37-2 backfill of pre-37-2
/// legacy rows).
///
/// <para><b>Not a security boundary.</b> A superuser / <c>ALTER TABLE … DISABLE
/// TRIGGER</c> bypasses it — which is exactly why the cryptographic hash-chain +
/// signed external-key checkpoints exist. The trigger stops accidental ORM
/// writes from silently rewriting history; the chain makes deliberate tampering
/// DETECTABLE. Document this in the runbook.</para>
/// </summary>
internal static class AuditRecordsAppendOnlyTrigger
{
    public const string UpSql = """
        CREATE OR REPLACE FUNCTION audit_records_append_only()
        RETURNS trigger AS $$
        BEGIN
          IF (TG_OP = 'DELETE') THEN
            RAISE EXCEPTION 'audit_records is append-only: DELETE rejected (row %).', OLD."Id";
          END IF;
          IF ( NEW."Id"                   IS DISTINCT FROM OLD."Id"
            OR NEW."ActionCode"           IS DISTINCT FROM OLD."ActionCode"
            OR NEW."Category"             IS DISTINCT FROM OLD."Category"
            OR NEW."Severity"             IS DISTINCT FROM OLD."Severity"
            OR NEW."ActorUserId"          IS DISTINCT FROM OLD."ActorUserId"
            OR NEW."ActorEmailSnapshot"   IS DISTINCT FROM OLD."ActorEmailSnapshot"
            OR NEW."TargetType"           IS DISTINCT FROM OLD."TargetType"
            OR NEW."TargetId"             IS DISTINCT FROM OLD."TargetId"
            OR NEW."Outcome"              IS DISTINCT FROM OLD."Outcome"
            OR NEW."IpAddress"            IS DISTINCT FROM OLD."IpAddress"
            OR NEW."UserAgent"            IS DISTINCT FROM OLD."UserAgent"
            OR NEW."OccurredAt"           IS DISTINCT FROM OLD."OccurredAt"
            OR NEW."SourceEventId"        IS DISTINCT FROM OLD."SourceEventId"
            OR NEW."SourceSequenceNumber" IS DISTINCT FROM OLD."SourceSequenceNumber"
            OR NEW."PayloadJson"          IS DISTINCT FROM OLD."PayloadJson"
            OR NEW."TenantId"             IS DISTINCT FROM OLD."TenantId"
            OR NEW."UserId"               IS DISTINCT FROM OLD."UserId"
            OR (OLD."RecordHash"     IS NOT NULL AND NEW."RecordHash"     IS DISTINCT FROM OLD."RecordHash")
            OR (OLD."PrevRecordHash" IS NOT NULL AND NEW."PrevRecordHash" IS DISTINCT FROM OLD."PrevRecordHash")
            OR (OLD."ChainSequence"  IS NOT NULL AND NEW."ChainSequence"  IS DISTINCT FROM OLD."ChainSequence")
          ) THEN
            RAISE EXCEPTION 'audit_records is append-only: forbidden UPDATE to immutable/chain fields (row %).', OLD."Id";
          END IF;
          RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;

        DROP TRIGGER IF EXISTS trg_audit_records_append_only ON audit_records;
        CREATE TRIGGER trg_audit_records_append_only
          BEFORE UPDATE OR DELETE ON audit_records
          FOR EACH ROW EXECUTE FUNCTION audit_records_append_only();
        """;

    public const string DownSql = """
        DROP TRIGGER IF EXISTS trg_audit_records_append_only ON audit_records;
        DROP FUNCTION IF EXISTS audit_records_append_only();
        """;
}
