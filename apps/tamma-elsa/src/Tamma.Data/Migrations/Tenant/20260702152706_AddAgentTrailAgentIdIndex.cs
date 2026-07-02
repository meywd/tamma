using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <summary>
    /// Story 32-6 (review I2) — index the per-agent action-trail lookup.
    /// <see cref="Repositories.EventRepository.QueryAgentTrailAsync"/> filters the
    /// tenant's <c>domain_events</c> (the 100%-audit stream) by
    /// <c>"Tags"-&gt;&gt;'agentId' = $1</c>. Without a supporting index that predicate is
    /// a sequential scan of the entire stream. A btree EXPRESSION index on
    /// <c>((Tags-&gt;&gt;'agentId'))</c> serves the equality directly (it matches the
    /// predicate expression exactly), and is far smaller than a whole-column GIN
    /// index on the jsonb <c>Tags</c>.
    ///
    /// <para>This is an "expression index the EF model cannot express" (same pattern
    /// as InitialControlPlane's partial/expression indexes), so it is raw SQL here —
    /// the model is deliberately unchanged, keeping the EF snapshot clean
    /// (<c>has-pending-model-changes</c> reports "No changes"). The unqualified table
    /// name resolves to the tenant schema via the per-tenant connection's
    /// <c>search_path</c>.</para>
    /// </summary>
    public partial class AddAgentTrailAgentIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_domain_events_tags_agentid
                  ON domain_events (("Tags"->>'agentId'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_domain_events_tags_agentid;");
        }
    }
}
