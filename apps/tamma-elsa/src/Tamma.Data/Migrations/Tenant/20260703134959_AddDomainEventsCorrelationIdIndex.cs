using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tamma.Data.Migrations.Tenant
{
    /// <summary>
    /// Story 32-23 (perf) — index the correlationId run lookups.
    /// <see cref="Repositories.EventRepository.ExistsByCorrelationIdAsync"/> (the
    /// streaming-tap ownership guard) and
    /// <see cref="Repositories.EventRepository.ListByCorrelationIdAsync"/> (replay)
    /// filter the tenant's <c>domain_events</c> (the 100%-audit stream) by
    /// <c>"Tags"-&gt;&gt;'correlationId' = $1</c>. Without a supporting index that
    /// predicate is a sequential scan of the entire stream. A btree EXPRESSION index
    /// on <c>((Tags-&gt;&gt;'correlationId'))</c> serves the equality directly (it
    /// matches the predicate expression exactly), and is far smaller than a
    /// whole-column GIN index on the jsonb <c>Tags</c>.
    ///
    /// <para>This is an "expression index the EF model cannot express" (same pattern
    /// as <see cref="AddAgentTrailAgentIdIndex"/>'s agentId index), so it is raw SQL
    /// here — the model is deliberately unchanged, keeping the EF snapshot clean
    /// (<c>has-pending-model-changes</c> reports "No changes"). The unqualified table
    /// name resolves to the tenant schema via the per-tenant connection's
    /// <c>search_path</c>.</para>
    /// </summary>
    public partial class AddDomainEventsCorrelationIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_domain_events_tags_correlationid
                  ON domain_events (("Tags"->>'correlationId'));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_domain_events_tags_correlationid;");
        }
    }
}
