using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.ControlPlane
{
    /// <summary>
    /// Story 43-14 (Amendment 2-A) — adds the grant SCOPE column to
    /// <c>action_authorizations</c>: <c>single-use</c> (default; today's CAS
    /// consume-once semantics, backfill-free — every existing row takes the
    /// default and keeps its behaviour) or <c>correlation-standing</c> (satisfies
    /// every ask in its correlation without being consumed).
    ///
    /// <para><b>Idempotent hand-written SQL — the same posture as
    /// <see cref="AddActionGovernance"/> (see that migration's doc).</b>
    /// <c>action_authorizations</c> is deliberately EXCLUDED from the Epic 19
    /// startup DROP list (it is safety policy), so this migration re-runs against
    /// a database where the column may already exist. A plain
    /// <c>migrationBuilder.AddColumn</c> would die with SqlState 42701 on the
    /// second deploy; <c>ADD COLUMN IF NOT EXISTS</c> + a guarded constraint add
    /// are re-runnable. The snapshot/model stays authoritative for EF.</para>
    /// </summary>
    public partial class AddAuthorizationScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE action_authorizations
                    ADD COLUMN IF NOT EXISTS "Scope" character varying(32) NOT NULL DEFAULT 'single-use';

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint
                        WHERE conname = 'ck_action_authorizations_scope'
                          AND conrelid = 'action_authorizations'::regclass
                    ) THEN
                        ALTER TABLE action_authorizations
                            ADD CONSTRAINT ck_action_authorizations_scope
                            CHECK ("Scope" IN ('single-use','correlation-standing'));
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE action_authorizations
                    DROP CONSTRAINT IF EXISTS ck_action_authorizations_scope;
                ALTER TABLE action_authorizations
                    DROP COLUMN IF EXISTS "Scope";
                """);
        }
    }
}
